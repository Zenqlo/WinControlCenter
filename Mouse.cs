using System.Text.Json;
using Gma.System.MouseKeyHook;
using System.Runtime.InteropServices;

namespace WinControlCenter
{
    public interface IMouseApi : ISystemApi
    {
        bool IsEnabled { get; set; }
        event EventHandler<object> StateChanged;
        Task<object> GetStateAsync();
        Task MoveMouseAsync(int deltaX, int deltaY);
        Task ClickMouseAsync(string button);
        Task ScrollMouseAsync(int delta);
    }

    public class MouseState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsEnabled { get; set; }
        public double Sensitivity { get; set; }
        public double ScrollSensitivity { get; set; }
        public double AccelerationFactor { get; set; }
        public double MaxAcceleration { get; set; }
    }

    /// <summary>
    /// Default mouse settings used throughout the application
    /// </summary>
    public static class MouseDefaults
    {
        public const double Sensitivity = 1.2;
        public const double ScrollSensitivity = 4.0;
        public const double AccelerationFactor = 1.5;
        public const double MaxAcceleration = 3.0;
    }

    public class MouseFeatureHandler : WebSocketFeatureHandler
    {
        private readonly IMouseApi _mouseApi;
        private readonly string _featureType = "mouse";

        protected override string GetStateMessageType => $"get{_featureType}";
        protected override string SetStateMessageType => _featureType;

        public MouseFeatureHandler(WebServer webServer, IMouseApi mouseApi) 
            : base(webServer, "mouse")
        {
            _mouseApi = mouseApi;
            _mouseApi.StateChanged += OnMouseStateChanged;
        }

        private void OnMouseStateChanged(object sender, object state) => _ = BroadcastStateAsync();

        protected override Task<object> GetStateAsync() => _mouseApi.GetStateAsync();

        protected override async Task HandleSetStateAsync(JsonElement valueElement)
        {
            if (valueElement.TryGetProperty("move", out var moveElement))
            {
                var deltaX = moveElement.GetProperty("x").GetInt32();
                var deltaY = moveElement.GetProperty("y").GetInt32();
                await _mouseApi.MoveMouseAsync(deltaX, deltaY);
            }

            if (valueElement.TryGetProperty("click", out var clickElement))
            {
                var button = clickElement.GetString();
                await _mouseApi.ClickMouseAsync(button);
            }

            if (valueElement.TryGetProperty("scroll", out var scrollElement))
            {
                var delta = scrollElement.GetInt32();
                await _mouseApi.ScrollMouseAsync(delta);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _mouseApi.StateChanged -= OnMouseStateChanged;
                _mouseApi.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class MouseKeyHookManager : IMouseApi
    {
        private IKeyboardMouseEvents _globalHook;
        private bool _isEnabled = true;
        private readonly object _stateLock = new object();
        private MouseState _currentState;

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x08;
        private const int MOUSEEVENTF_RIGHTUP = 0x10;
        private const int MOUSEEVENTF_WHEEL = 0x0800;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    if (_isEnabled)
                    {
                        _globalHook = Hook.GlobalEvents();
                    }
                    else
                    {
                        _globalHook?.Dispose();
                        _globalHook = null;
                    }
                    UpdateState();
                }
            }
        }

        public event EventHandler<object> StateChanged;

        public MouseKeyHookManager()
        {
            _globalHook = Hook.GlobalEvents();
            _currentState = new MouseState
            {
                X = 0,
                Y = 0,
                IsEnabled = true,
                Sensitivity = MouseDefaults.Sensitivity,
                ScrollSensitivity = MouseDefaults.ScrollSensitivity,
                AccelerationFactor = MouseDefaults.AccelerationFactor,
                MaxAcceleration = MouseDefaults.MaxAcceleration
            };
            InitializeMouseState();
        }

        private void InitializeMouseState()
        {
            try
            {
                var currentPosition = System.Windows.Forms.Cursor.Position;
                _currentState = new MouseState
                {
                    X = currentPosition.X,
                    Y = currentPosition.Y,
                    IsEnabled = true,
                    Sensitivity = MouseDefaults.Sensitivity,
                    ScrollSensitivity = MouseDefaults.ScrollSensitivity,
                    AccelerationFactor = MouseDefaults.AccelerationFactor,
                    MaxAcceleration = MouseDefaults.MaxAcceleration
                };
                UpdateState();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize mouse state: {ex.Message}");
            }
        }

        private void UpdateState()
        {
            lock (_stateLock)
            {
                var currentPosition = System.Windows.Forms.Cursor.Position;
                _currentState.X = currentPosition.X;
                _currentState.Y = currentPosition.Y;
                StateChanged?.Invoke(this, _currentState);
            }
        }

        public Task<object> GetStateAsync()
        {
            try
            {
                return Task.FromResult<object>(_currentState);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get mouse state: {ex.Message}");
                return Task.FromResult<object>(new MouseState());
            }
        }

        public void UpdateSensitivitySettings(double sensitivity, double scrollSensitivity, double accelerationFactor, double maxAcceleration)
        {
            lock (_stateLock)
            {
                _currentState.Sensitivity = sensitivity;
                _currentState.ScrollSensitivity = scrollSensitivity;
                _currentState.AccelerationFactor = accelerationFactor;
                _currentState.MaxAcceleration = maxAcceleration;
                StateChanged?.Invoke(this, _currentState);
            }
        }

        public MouseState GetMouseState()
        {
            try
            {
                var state = (MouseState)GetStateAsync().Result;
                Logger.Log($"Getting mouse state: Sensitivity={state.Sensitivity}, ScrollSensitivity={state.ScrollSensitivity}, AccelerationFactor={state.AccelerationFactor}, MaxAcceleration={state.MaxAcceleration}");
                return state;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting mouse state: {ex.Message}");
                return new MouseState
                {
                    IsEnabled = false,
                    Sensitivity = MouseDefaults.Sensitivity,
                    ScrollSensitivity = MouseDefaults.ScrollSensitivity,
                    AccelerationFactor = MouseDefaults.AccelerationFactor,
                    MaxAcceleration = MouseDefaults.MaxAcceleration
                };
            }
        }

        public Task MoveMouseAsync(int deltaX, int deltaY)
        {
            if (_isEnabled)
            {
                var currentPosition = System.Windows.Forms.Cursor.Position;
                var speed = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                var acceleration = Math.Min(
                    1 + (speed / 50) * (_currentState.AccelerationFactor - 1),
                    _currentState.MaxAcceleration
                );

                var adjustedDeltaX = (int)(deltaX * _currentState.Sensitivity * acceleration);
                var adjustedDeltaY = (int)(deltaY * _currentState.Sensitivity * acceleration);

                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
                    currentPosition.X + adjustedDeltaX,
                    currentPosition.Y + adjustedDeltaY
                );
            }
            return Task.CompletedTask;
        }

        public Task ClickMouseAsync(string button)
        {
            if (!_isEnabled) return Task.CompletedTask;

            try
            {
                switch (button.ToLower())
                {
                    case "left":
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        Thread.Sleep(50);  // Increased delay to prevent double-click interpretation
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        break;
                    case "right":
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                        Thread.Sleep(50);  // Increased delay for right click as well
                        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error performing mouse click: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task ScrollMouseAsync(int delta)
        {
            if (!_isEnabled) return Task.CompletedTask;

            try
            {
                var adjustedDelta = (int)(delta * _currentState.ScrollSensitivity);
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, adjustedDelta, 0);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error performing mouse scroll: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _globalHook?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public static class MouseFactory
    {
        public static IMouseApi CreateMouseApi() => new MouseKeyHookManager();
    }
}
