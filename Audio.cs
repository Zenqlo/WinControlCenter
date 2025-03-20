using System.Text.Json;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace WinControlCenter
{
    public interface IAudioApi : ISystemApi
    {
        bool IsEnabled { get; set; }
        new event EventHandler<object> StateChanged;
        new Task<object> GetStateAsync();
        Task SetVolumeAsync(float volume);
        Task SetMuteAsync(bool muted);
        Task SetDeviceAsync(string deviceId);
        Task<IList<AudioDevice>> GetDevicesAsync();
    }

    public class AudioDevice
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public bool IsDefault { get; set; }
    }

    public class AudioState
    {
        public float Volume { get; set; }
        public bool Muted { get; set; }
        public required IList<AudioDevice> AvailableDevices { get; set; }
    }

    public class AudioFeatureHandler : WebSocketFeatureHandler
    {
        private readonly IAudioApi _audioApi;

        public AudioFeatureHandler(WebServer webServer, IAudioApi audioApi, string featureType) 
            : base(webServer, featureType)
        {
            _audioApi = audioApi;
            _audioApi.StateChanged += OnAudioStateChanged;
        }

        private void OnAudioStateChanged(object? sender, object state) => _ = BroadcastStateAsync();

        protected override Task<object> GetStateAsync() => _audioApi.GetStateAsync();

        protected override async Task HandleSetStateAsync(JsonElement valueElement)
        {
            if (valueElement.TryGetProperty("value", out var volumeElement))
            {
                await _audioApi.SetVolumeAsync(volumeElement.GetSingle());
            }

            if (valueElement.TryGetProperty("muted", out var mutedElement))
            {
                await _audioApi.SetMuteAsync(mutedElement.GetBoolean());
            }

            if (valueElement.TryGetProperty("deviceId", out var deviceIdElement))
            {
                var deviceId = deviceIdElement.GetString();
                if (!string.IsNullOrEmpty(deviceId))
                {
                    await _audioApi.SetDeviceAsync(deviceId);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _audioApi.StateChanged -= OnAudioStateChanged;
                _audioApi.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class AudioSwitcherManager : IAudioApi
    {
        private CoreAudioController _controller;
        private readonly DeviceType _deviceType;
        private CoreAudioDevice _currentDevice;
        private bool _isEnabled = true;
        private readonly object _stateLock = new object();
        private IDisposable _volumeChangedSubscription;
        private IDisposable _muteChangedSubscription;
        private System.Timers.Timer _debounceTimer;
        private const int DEBOUNCE_INTERVAL = 40; // 40ms debounce interval

        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }

        public new event EventHandler<object> StateChanged;

        public AudioSwitcherManager(DeviceType deviceType)
        {
            _deviceType = deviceType;
            
            // Initialize debounce timer
            _debounceTimer = new System.Timers.Timer(DEBOUNCE_INTERVAL);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += (s, e) => 
            {
                lock (_stateLock)
                {
                    var state = CreateCurrentState();
                    StateChanged?.Invoke(this, state);
                }
            };

            InitializeCoreAudio();
        }

        private void InitializeCoreAudio(bool isReinitializing = false)
        {
            Logger.Log($"{(isReinitializing ? "Re-initializing" : "Initializing")} CoreAudio API");
            
            // Cleanup if reinitializing
            if (isReinitializing)
            {
                UnsubscribeFromDeviceEvents();
                _controller?.Dispose();
            }

            _controller = new CoreAudioController();
            
            // Give the controller time to initialize
            Task.Delay(100).Wait();
            
            _controller.AudioDeviceChanged.Subscribe(new DeviceChangedHandler(this));

            // Try to initialize device multiple times if needed
            for (int i = 0; i < 3; i++)
            {
                var device = GetDefaultDevice();
                if (device != null && !string.IsNullOrWhiteSpace(device.Name) && device.Name != "Unknown")
                {
                    if (_currentDevice?.Id != device.Id)
                    {
                        UnsubscribeFromDeviceEvents();
                        _currentDevice = device;
                        SubscribeToDeviceEvents();
                        UpdateState();
                        Logger.Log($"Successfully initialized {_deviceType} device: {device.Name} (ID: {device.Id})");
                    }
                    return;
                }
                Task.Delay(50).Wait();
            }
            
            Logger.Log($"Failed to initialize CoreAudio API with a valid {_deviceType} device");
        }

        private void SubscribeToDeviceEvents()
        {
            if (_currentDevice != null)
            {
                _volumeChangedSubscription = _currentDevice.VolumeChanged.Subscribe(new VolumeChangedHandler(this));
                _muteChangedSubscription = _currentDevice.MuteChanged.Subscribe(new MuteChangedHandler(this));
            }
        }

        private void UnsubscribeFromDeviceEvents()
        {
            if (_currentDevice != null)
            {
                Logger.Log($"Unsubscribing from events for {_deviceType} device: {_currentDevice.Name}");
            }
            _volumeChangedSubscription?.Dispose();
            _muteChangedSubscription?.Dispose();
            _volumeChangedSubscription = null;
            _muteChangedSubscription = null;
        }

        private class DeviceChangedHandler : IObserver<DeviceChangedArgs>
        {
            private readonly AudioSwitcherManager _manager;
            public DeviceChangedHandler(AudioSwitcherManager manager) => _manager = manager;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(DeviceChangedArgs args)
            {
                if (args.Device.IsDefaultDevice && args.Device.DeviceType == _manager._deviceType)
                {
                    Logger.Log($"Default {_manager._deviceType} device changed to: {args.Device.Name}");
                    _manager.InitializeDefaultDevice();
                }
            }
        }

        private class VolumeChangedHandler : IObserver<DeviceVolumeChangedArgs>
        {
            private readonly AudioSwitcherManager _manager;
            public VolumeChangedHandler(AudioSwitcherManager manager) => _manager = manager;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(DeviceVolumeChangedArgs args)
            {                
                _manager.UpdateState();
            }
        }

        private class MuteChangedHandler : IObserver<DeviceMuteChangedArgs>
        {
            private readonly AudioSwitcherManager _manager;
            public MuteChangedHandler(AudioSwitcherManager manager) => _manager = manager;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(DeviceMuteChangedArgs args)
            {
                Logger.Log($"Mute state changed externally for {_manager._deviceType}: {args.IsMuted}");
                _manager.UpdateState();
            }
        }

        private CoreAudioDevice GetDefaultDevice()
        {
            var device = _deviceType == DeviceType.Capture ? 
                _controller.DefaultCaptureDevice : 
                _controller.DefaultPlaybackDevice;            
            return device;
        }

        private void InitializeDefaultDevice()
        {
            try
            {
                var newDevice = GetDefaultDevice();
                if (newDevice == null)
                {
                    Logger.Log($"No default {_deviceType} device found");
                    return;
                }

                // If we get an Unknown device, try reinitializing the entire CoreAudio API
                if (string.IsNullOrWhiteSpace(newDevice.Name) || newDevice.Name == "Unknown")
                {
                    Logger.Log($"Unknown {_deviceType} device detected - attempting CoreAudio API reinitialization");
                    
                    // Try reinitializing twice
                    for (int i = 0; i < 2; i++)
                    {
                        InitializeCoreAudio(isReinitializing: true);
                        
                        // Check if initialization was successful
                        newDevice = GetDefaultDevice();
                        if (newDevice != null && !string.IsNullOrWhiteSpace(newDevice.Name) && newDevice.Name != "Unknown")
                        {
                            return;
                        }
                    }
                }

                if (_currentDevice?.Id != newDevice.Id)
                {
                    Logger.Log($"Initializing new default {_deviceType} device: {newDevice.Name} (ID: {newDevice.Id})");
                    UnsubscribeFromDeviceEvents();
                    _currentDevice = newDevice;
                    SubscribeToDeviceEvents();
                    UpdateState();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize default {_deviceType} device: {ex.Message}");
            }
        }

        private void UpdateState()
        {
            // Reset and start the debounce timer
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private float GetCurrentVolume() => 
            (float)(_currentDevice?.Volume ?? 0) / 100f;
            
        private AudioState CreateCurrentState()
        {
            return new AudioState
            {
                Volume = GetCurrentVolume(),
                Muted = _currentDevice?.IsMuted ?? false,
                AvailableDevices = GetDevicesAsync().Result
            };
        }

        public new Task<object> GetStateAsync()
        {
            try
            {
                return Task.FromResult<object>(CreateCurrentState());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get audio state: {ex.Message}");
                return Task.FromResult<object>(new AudioState
                {
                    Volume = 0,
                    Muted = false,
                    AvailableDevices = new List<AudioDevice>()
                });
            }
        }

        public Task SetVolumeAsync(float volume)
        {
            if (_currentDevice != null)
            {
                _currentDevice.Volume = volume * 100;
                UpdateState();
            }
            return Task.CompletedTask;
        }

        public Task SetMuteAsync(bool muted)
        {
            if (_currentDevice != null)
            {
                Logger.Log($"Setting {_deviceType} mute state to: {muted}");
                _currentDevice.Mute(muted);
                UpdateState();
            }
            return Task.CompletedTask;
        }

        public Task SetDeviceAsync(string deviceId)
        {
            var device = _controller.GetDevice(new Guid(deviceId));
            if (device != null)
            {
                Logger.Log($"Setting default {_deviceType} device to: {device.Name} (ID: {deviceId})");
                device.SetAsDefault();
                _currentDevice = device;
                UpdateState();
            }
            return Task.CompletedTask;
        }

        private IEnumerable<CoreAudioDevice> GetActiveDevices()
        {
            return _deviceType == DeviceType.Playback ?
                _controller.GetPlaybackDevices() :
                _controller.GetCaptureDevices();
        }

        public Task<IList<AudioDevice>> GetDevicesAsync()
        {
            try
            {
                var defaultDevice = GetDefaultDevice();

                var devices = GetActiveDevices()
                    .Where(d => d.State == DeviceState.Active)
                    .Select(device => 
                    {
                        string deviceName = "Unknown";
                        if (!string.IsNullOrWhiteSpace(device.FullName))
                            deviceName = device.FullName;
                        else if (!string.IsNullOrWhiteSpace(device.Name))
                            deviceName = device.Name;
                        else if (!string.IsNullOrWhiteSpace(device.InterfaceName))
                            deviceName = device.InterfaceName;

                        var isDefault = device.Id == defaultDevice?.Id;                        

                        return new AudioDevice
                        {
                            Id = device.Id.ToString(),
                            Name = deviceName,
                            IsDefault = isDefault
                        };
                    })
                    .ToList();
                
                return Task.FromResult<IList<AudioDevice>>(devices);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to enumerate {_deviceType} devices: {ex.Message}");
                return Task.FromResult<IList<AudioDevice>>(new List<AudioDevice>());
            }
        }

        public void Dispose()
        {
            Logger.Log($"Disposing AudioSwitcherManager for {_deviceType}");
            // Unsubscribe from device events
            UnsubscribeFromDeviceEvents();
            
            // Dispose of the debounce timer
            _debounceTimer?.Dispose();
            
            _controller.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public static class AudioFactory
    {
        public static IAudioApi CreateSpeakerApi() => 
            new AudioSwitcherManager(DeviceType.Playback);

        public static IAudioApi CreateMicrophoneApi() => 
            new AudioSwitcherManager(DeviceType.Capture);
    }
} 