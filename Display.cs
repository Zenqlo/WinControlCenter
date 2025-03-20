/*
 * Based on original code from:
 *
 * (c) Mohammad Elsheimy
 * Changing Display Settings Programmatically
 * 
 * https://www.c-sharpcorner.com/uploadfile/GemingLeader/changing-display-settings-programmatically/
 *
 * Added support for:
 *
 * * Listing Monitors and Monitor specific display modes
 * * Set display mode for a specific monitor/driver
 * 
 * Based on sample code from:
 * https://github.com/RickStrahl/SetResolution
*/

using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinControlCenter.SetResolution
{
    public interface IDisplayApi : ISystemApi
    {
        bool IsEnabled { get; set; }
        Task<object> GetStateAsync();
        Task SetDisplaySettingsAsync(int monitorId, int width, int height, int frequency, int orientation, bool noPersist);
    }

    public class DisplayState
    {
        public bool IsEnabled { get; set; }
        public List<object> Displays { get; set; } = new List<object>();
    }

    public class DisplayApiImpl : IDisplayApi
    {
        private bool _isEnabled = true;
        private readonly object _stateLock = new object();

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    StateChanged?.Invoke(this, GetStateAsync().Result);
                }
            }
        }

        public event EventHandler<object> StateChanged;

        public async Task<object> GetStateAsync()
        {
            if (!_isEnabled)
            {
                return new DisplayState { IsEnabled = false };
            }

            var devices = DisplayManager.GetAllDisplayDevices();
            var currentSettings = new List<object>();

            foreach (var device in devices)
            {
                var settings = DisplayManager.GetCurrentDisplaySetting(device.DriverDeviceName);
                var allModes = DisplayManager.GetAllDisplaySettings(device.DriverDeviceName)
                    .Where(mode => mode.Width > 0 && mode.Height > 0) // Filter out invalid modes
                    .GroupBy(d => new { d.Width, d.Height })
                    .Select(g => new {
                        Width = g.Key.Width,
                        Height = g.Key.Height,
                        Frequencies = g.Select(m => m.Frequency).Distinct().OrderByDescending(f => f).ToList()
                    })
                    .OrderByDescending(d => d.Width)
                    .ToList();

                currentSettings.Add(new
                {
                    device.Index,
                    device.DisplayName,
                    device.IsPrimary,
                    device.IsSelected,
                    CurrentMode = new
                    {
                        settings.Width,
                        settings.Height,
                        settings.Frequency,
                        settings.BitCount,
                        settings.Orientation
                    },
                    AvailableModes = allModes
                });
            }

            return new DisplayState
            {
                IsEnabled = _isEnabled,
                Displays = currentSettings
            };
        }

        public async Task SetDisplaySettingsAsync(int monitorId, int width, int height, int frequency, int orientation, bool noPersist)
        {
            if (!_isEnabled)
                return;

            var devices = DisplayManager.GetAllDisplayDevices();
            var device = devices.FirstOrDefault(d => d.Index == monitorId);
            
            if (device == null)
                throw new InvalidOperationException("Monitor not found");

            Logger.Log($"Applying display settings for monitor {monitorId} ({device.DisplayName}): {width}x{height}@{frequency}Hz, Orientation: {orientation}, NoPersist: {noPersist}");

            var settings = new DisplaySettings
            {
                Width = width,
                Height = height,
                Frequency = frequency,
                BitCount = 32,
                Orientation = (Orientation)orientation,
                NoPersist = noPersist
            };

            try 
            {
                DisplayManager.SetDisplaySettings(settings, device.DriverDeviceName);
                Logger.Log($"Successfully applied display settings for monitor {monitorId} ({device.DisplayName})");
                StateChanged?.Invoke(this, await GetStateAsync());
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to apply display settings for monitor {monitorId}: {ex.Message}");
                throw new InvalidOperationException($"Failed to apply display settings: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Nothing to dispose
            GC.SuppressFinalize(this);
        }
    }

    public static class DisplayFactory
    {
        public static IDisplayApi CreateDisplayApi() => new DisplayApiImpl();
    }


    /// Represents wrapper to native methods.
    public static class DisplayManager
    {        
        /// Returns DisplaySettings object encapsulates current display settings.        
        public static DisplaySettings GetCurrentSettings(string deviceName = null)
        {
            return CreateDisplaySettingsObject(-1, GetDeviceMode(deviceName));
        }

        
        /// Changes current display settings with new settings provided. May throw InvalidOperationException if failed. Check exception message for details.        
        /// <param name="set">The new settings.</param>        
        /// Internally calls ChangeDisplaySettings() native function.        
        public static void SetDisplaySettings(DisplaySettings set, string deviceName = null)
        {
            DisplayManagerNative.DEVMODE mode = GetDeviceMode(deviceName);

            mode.dmPelsWidth = (uint)set.Width;
            mode.dmPelsHeight = (uint)set.Height;
            mode.dmDisplayOrientation = (uint)set.Orientation;
            mode.dmBitsPerPel = (uint)set.BitCount;
            mode.dmDisplayFrequency = (uint)set.Frequency;
            mode.dmFields = DisplayManagerNative.DmFlags.DM_PELSWIDTH | 
                           DisplayManagerNative.DmFlags.DM_PELSHEIGHT | 
                           DisplayManagerNative.DmFlags.DM_DISPLAYFREQUENCY | 
                           DisplayManagerNative.DmFlags.DM_BITSPERPEL | 
                           DisplayManagerNative.DmFlags.DM_DISPLAYORIENTATION;

            uint CDS_UPDATEREGISTRY = set.NoPersist ? 0u : 1;   // force to persist settings in registry
            DisplayChangeResult result = (DisplayChangeResult)DisplayManagerNative.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            
            string msg = null;
            switch (result)
            {
                case DisplayChangeResult.BadDualView:
                    msg = "The settings change was unsuccessful because system is DualView capable.";
                    break;
                case DisplayChangeResult.BadParam:
                    msg = "An invalid parameter was passed in.";
                    break;
                case DisplayChangeResult.BadFlags:
                    msg = "An invalid set of flags was passed in.";
                    break;
                case DisplayChangeResult.NotUpdated:
                    msg = "Unable to write settings to the registry.";
                    break;
                case DisplayChangeResult.BadMode:
                    msg = "The graphics mode is not supported.";
                    break;
                case DisplayChangeResult.Failed:
                    msg = "The display driver failed the specified graphics mode.";
                    break;
                case DisplayChangeResult.Restart:
                    msg = "The computer must be restarted for the graphics mode to work.";
                    break;
            }

            if (msg != null)
                throw new InvalidOperationException(msg);
        }



        /// Returns current display mode setting
        public static DisplaySettings GetCurrentDisplaySetting(string deviceName = null)
        {
            var mode = GetDeviceMode(deviceName);
            var settings = CreateDisplaySettingsObject(0, mode);            
            return settings;
        }


        /// Returns list of all display settings
        public static List<DisplaySettings> GetAllDisplaySettings(string deviceName = null)
        {
            var list = new List<DisplaySettings>();
            DisplayManagerNative.DEVMODE mode = new DisplayManagerNative.DEVMODE();

            mode.Initialize();

            int idx = 0;
            
            while (DisplayManagerNative.EnumDisplaySettings(DisplayManagerNative.ToLPTStr(deviceName), idx, ref mode))
            //while (DisplayManagerNative.EnumDisplaySettings(deviceName, idx, ref mode))
                    list.Add(CreateDisplaySettingsObject(idx++, mode));

            return list;
        }

        public static List<DisplayDevice> GetAllDisplayDevices()
        {
            var list = new List<DisplayDevice>();
            uint idx = 0;
            uint size = 256;

            var device = new DisplayManagerNative.DISPLAY_DEVICE();
            device.cb = Marshal.SizeOf(device);
            int displayIndex = 0;            

            while (DisplayManagerNative.EnumDisplayDevices(null, idx, ref device, size) )
            {
                if (device.StateFlags.HasFlag(DisplayManagerNative.DisplayDeviceStateFlags.AttachedToDesktop))
                {
                    var isPrimary = device.StateFlags.HasFlag(DisplayManagerNative.DisplayDeviceStateFlags.PrimaryDevice);
                    var deviceName = device.DeviceName;
                    
                    DisplayManagerNative.EnumDisplayDevices(device.DeviceName, 0, ref device, 0);
                    displayIndex++;
                    var dev = new DisplayDevice()
                    {
                        Index = displayIndex,
                        Id = device.DeviceID,
                        MonitorDeviceName = device.DeviceName,
                        DriverDeviceName = deviceName,
                        DisplayName = device.DeviceString,
                        IsPrimary = isPrimary,
                        IsSelected = isPrimary
                    };
                    
                    list.Add(dev);
                }

                idx++;

                device = new DisplayManagerNative.DISPLAY_DEVICE();
                device.cb = Marshal.SizeOf(device);
            }

            Logger.Log($"Found {list.Count} display device(s)");
            return list;
        }

        public class DisplayDevice
        {
            public int Index { get; set; }
            public string MonitorDeviceName { get; set;  }
            public string Id { get; set; }
            public string DriverDeviceName { get; set; }
            public string DisplayName { get; set; }
            public bool IsPrimary { get; set; }
            public bool IsSelected { get; set; }

            public override string ToString()
            {
                return $"{Index} {DisplayName}{(IsSelected ? " *" : "")}{(IsPrimary ? " (Main)" : "")}";
            }
        }


        /// Rotates the screen from its current location by 90 degrees either clockwise or anti-clockwise.
        /// <param name="clockwise">Set to true to rotate the screen 90 degrees clockwise from its current location, or false to rotate it anti-clockwise.</param>
        public static void RotateScreen(bool clockwise)
        {
            DisplaySettings set = DisplayManager.GetCurrentSettings();

            int tmp = set.Height;
            set.Height = set.Width;
            set.Width = tmp;

            if (clockwise)
                set.Orientation++;
            else
                set.Orientation--;

            if (set.Orientation < Orientation.Default)
                set.Orientation = Orientation.Rotate270;
            else if (set.Orientation > Orientation.Rotate270)
                set.Orientation = Orientation.Default;

            SetDisplaySettings(set);
        }


        
        /// A private helper methods used to derive a DisplaySettings object from the DEVMODE structure.        
        /// <param name="idx">The mode index attached with the settings. Starts form zero. Is -1 for the current settings.</param>
        /// <param name="mode">The current DEVMODE object represents the display information to derive the DisplaySettings object from.</param>
        private static DisplaySettings CreateDisplaySettingsObject(int idx, DisplayManagerNative.DEVMODE mode)
        {
            return new DisplaySettings()
            {
                Index = idx,
                Width = (int)mode.dmPelsWidth,
                Height = (int)mode.dmPelsHeight,
                Orientation = (Orientation)mode.dmDisplayOrientation,
                BitCount = (int)mode.dmBitsPerPel,
                Frequency = (int)mode.dmDisplayFrequency
            };
        }

        /// A private helper method used to retrieve current display settings as a DEVMODE object.
        /// Internally calls EnumDisplaySettings() native function with the value ENUM_CURRENT_SETTINGS (-1) to retrieve the current settings.
        private static DisplayManagerNative.DEVMODE GetDeviceMode(string deviceName = null)
        {
            var mode = new DisplayManagerNative.DEVMODE();

            mode.Initialize();

            if (DisplayManagerNative.EnumDisplaySettings(DisplayManagerNative.ToLPTStr(deviceName), DisplayManagerNative.ENUM_CURRENT_SETTINGS, ref mode))
                return mode;
            else
                throw new InvalidOperationException(GetLastError());
        }

        private static string GetLastError()
        {
            int err = Marshal.GetLastWin32Error();

            if (DisplayManagerNative.FormatMessage(DisplayManagerNative.FORMAT_MESSAGE_FLAGS,
                    DisplayManagerNative.FORMAT_MESSAGE_FROM_HMODULE,
                    (uint)err, 0, out var msg, 0, 0) == 0)
                return "A fatal error occurred while changing display settings.";
            else
                return msg;
        }
    }

    public enum Orientation
    {
        Default = 0,
        Rotate90 = 1,
        Rotate180 = 2,
        Rotate270 = 3
    }

    public class DisplaySettings
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Orientation Orientation { get; set; }
        public int BitCount { get; set; }
        public int Frequency { get; set; }

        /// Determines whether the settings are stored in the registry for
        /// persistence for a reboot.
        public bool NoPersist { get; set; }

        /// Display Mode string display with full detail
        public override string ToString()
        {
            return ToString(false);
        }

        /// Display Mode string display
        /// <param name="noDetails">only return height and width</param>        
        public string ToString(bool noDetails)
        {
            if (noDetails)
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, $"{Width} x {Height}");
            }

            return string.Format(System.Globalization.CultureInfo.CurrentCulture,
                $"{Width} x {Height}, {Frequency}hz, {BitCount}bit{(Orientation != Orientation.Default ? ", " + Orientation.ToString() : "")}");
        }


        public override bool Equals(object d)
        {
            var disp = d as DisplaySettings;
            return (disp.Width == Width && disp.Height == Height &&
                    disp.Frequency == Frequency &&
                    disp.BitCount == BitCount &&
                    disp.Orientation == Orientation);
        }


        public override int GetHashCode()
        {
            return ("" + "W" + Width + "H" + Height + "F" + Frequency + "B" + BitCount + "O" + Orientation)
                .GetHashCode();
        }
    }

    enum DisplayChangeResult
    {

        /// Windows XP: The settings change was unsuccessful because system is DualView capable.
        BadDualView = -6,

        /// An invalid parameter was passed in. This can include an invalid flag or combination of flags.
        BadParam = -5,

        /// An invalid set of flags was passed in.
        BadFlags = -4,

        /// Windows NT/2000/XP: Unable to write settings to the registry.
        NotUpdated = -3,

        /// The graphics mode is not supported.
        BadMode = -2,

        /// The display driver failed the specified graphics mode.
        Failed = -1,

        /// The settings change was successful.
        Successful = 0,

        /// The computer must be restarted in order for the graphics mode to work.
        Restart = 1
    }


}


namespace WinControlCenter.SetResolution
{
    static class DisplayManagerNative
    {
        #region Enum Display Settings

        [Flags()]
        public enum DmFlags : uint
        {
            DM_ORIENTATION = 0x00000001,
            DM_PAPERSIZE = 0x00000002,
            DM_PAPERLENGTH = 0x00000004,
            DM_PAPERWIDTH = 0x00000008,
            DM_SCALE = 0x00000010,
            DM_POSITION = 0x00000020,
            DM_NUP = 0x00000040,
            DM_DISPLAYORIENTATION = 0x00000080,
            DM_COPIES = 0x00000100,
            DM_DEFAULTSOURCE = 0x00000200,
            DM_PRINTQUALITY = 0x00000400,
            DM_COLOR = 0x00000800,
            DM_DUPLEX = 0x00001000,
            DM_YRESOLUTION = 0x00002000,
            DM_TTOPTION = 0x00004000,
            DM_COLLATE = 0x00008000,
            DM_FORMNAME = 0x00010000,
            DM_LOGPIXELS = 0x00020000,
            DM_BITSPERPEL = 0x00040000,
            DM_PELSWIDTH = 0x00080000,
            DM_PELSHEIGHT = 0x00100000,
            DM_DISPLAYFLAGS = 0x00200000,
            DM_DISPLAYFREQUENCY = 0x00400000,
            DM_ICMMETHOD = 0x00800000,
            DM_ICMINTENT = 0x01000000,
            DM_MEDIATYPE = 0x02000000,
            DM_DITHERTYPE = 0x04000000,
            DM_PANNINGWIDTH = 0x08000000,
            DM_PANNINGHEIGHT = 0x10000000,
            DM_DISPLAYFIXEDOUTPUT = 0x20000000
        }

        [DllImport("User32.dll", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(
            byte[] lpszDeviceName,  // display device
            [param: MarshalAs(UnmanagedType.U4)]
            int iModeNum,         // graphics mode
            [In, Out]
            ref DEVMODE lpDevMode       // graphics mode settings
        );

        [DllImport("user32.dll")]
        public static extern int ChangeDisplaySettingsEx(
            string lpszDeviceName,
            ref DEVMODE lpDevMode,
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DMDO_DEFAULT = 0;
        public const int DMDO_90 = 1;
        public const int DMDO_180 = 2;
        public const int DMDO_270 = 3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public DmFlags dmFields;
            public POINTL dmPosition;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;

            public void Initialize()
            {
                this.dmDeviceName = new string(new char[32]);
                this.dmFormName = new string(new char[32]);
                this.dmSize = (ushort)Marshal.SizeOf(this);
            }
        }

        // 8-bytes structure
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL
        {
            public int x;
            public int y;
        }

        #endregion


        #region Enum DisplayDevices
        
        [DllImport("user32.dll")]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)]
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            [MarshalAs(UnmanagedType.U4)]
            public DisplayDeviceStateFlags StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [Flags()]
        public enum DisplayDeviceStateFlags : int
        {
            /// The device is part of the desktop.
            AttachedToDesktop = 0x1,
            MultiDriver = 0x2,
            /// The device is part of the desktop.
            PrimaryDevice = 0x4,
            /// Represents a pseudo device used to mirror application drawing for remoting or other purposes.
            MirroringDriver = 0x8,
            /// The device is VGA compatible.
            VGACompatible = 0x10,
            /// The device is removable; it cannot be the primary display.
            Removable = 0x20,
            /// The device has more display modes than its output devices support.
            ModesPruned = 0x8000000,
            Remote = 0x4000000,
            Disconnect = 0x2000000
        }
        #endregion


        #region Errors

        [DllImport("User32.dll", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        public static extern int ChangeDisplaySettings(
            [In, Out]
            ref DEVMODE lpDevMode,
            [param: MarshalAs(UnmanagedType.U4)]
            uint dwflags);


        [DllImport("kernel32.dll", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern uint FormatMessage(
            [param: MarshalAs(UnmanagedType.U4)]
            uint dwFlags,
            [param: MarshalAs(UnmanagedType.U4)]
            uint lpSource,
            [param: MarshalAs(UnmanagedType.U4)]
            uint dwMessageId,
            [param: MarshalAs(UnmanagedType.U4)]
            uint dwLanguageId,
            [param: MarshalAs(UnmanagedType.LPTStr)]
            out string lpBuffer,
            [param: MarshalAs(UnmanagedType.U4)]
            uint nSize,
            [param: MarshalAs(UnmanagedType.U4)]
            uint arguments);

        public const uint FORMAT_MESSAGE_FROM_HMODULE = 0x800;

        public const uint FORMAT_MESSAGE_ALLOCATE_BUFFER = 0x100;
        public const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x200;
        public const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x1000;
        public const uint FORMAT_MESSAGE_FLAGS = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_IGNORE_INSERTS | FORMAT_MESSAGE_FROM_SYSTEM;

        #endregion

        #region Helpers

        public static byte[] ToLPTStr(string str)
        {
            if (str == null) return null;

            var lptArray = new byte[str.Length + 1];

            var index = 0;
            foreach (char c in str.ToCharArray())
                lptArray[index++] = Convert.ToByte(c);

            lptArray[index] = Convert.ToByte('\0');

            return lptArray;
        }

        #endregion
    }

}


namespace WinControlCenter.SetResolution
{
    public class DisplayFeatureHandler : WebSocketFeatureHandler
    {
        private readonly IDisplayApi _displayApi;

        protected override string GetStateMessageType => "getdisplay";
        protected override string SetStateMessageType => "display";

        public DisplayFeatureHandler(WebServer webServer, IDisplayApi displayApi) 
            : base(webServer, "display")
        {            
            _displayApi = displayApi;
            _displayApi.StateChanged += OnDisplayStateChanged;
        }

        private void OnDisplayStateChanged(object sender, object state)
        {
            Logger.Log("Display state changed - broadcasting update to clients");
            _ = BroadcastStateAsync();
        }

        protected override Task<object> GetStateAsync() => _displayApi.GetStateAsync();

        protected override async Task HandleSetStateAsync(JsonElement valueElement)
        {
            try
            {
                var monitorId = valueElement.GetProperty("monitorId").GetInt32();
                var width = valueElement.GetProperty("width").GetInt32();
                var height = valueElement.GetProperty("height").GetInt32();
                
                // Handle optional properties with defaults
                var frequency = valueElement.TryGetProperty("frequency", out var f) ? f.GetInt32() : 60;
                var orientation = valueElement.TryGetProperty("orientation", out var o) ? o.GetInt32() : 0;
                var noPersist = valueElement.TryGetProperty("noPersist", out var np) ? np.GetBoolean() : false;

                Logger.Log($"Received display settings change request from client");

                // Get current display settings to check current orientation
                var state = await _displayApi.GetStateAsync() as DisplayState;
                if (state != null && state.Displays.Count > 0)
                {
                    // Find the current monitor
                    var currentMonitor = state.Displays.FirstOrDefault(d => 
                        d.GetType().GetProperty("Index")?.GetValue(d) is int index && 
                        index == monitorId);

                    if (currentMonitor != null)
                    {
                        // Get current orientation
                        var currentMode = currentMonitor.GetType().GetProperty("CurrentMode")?.GetValue(currentMonitor);
                        if (currentMode != null)
                        {
                            var currentOrientation = (int)(currentMode.GetType().GetProperty("Orientation")?.GetValue(currentMode) ?? 0);
                            
                            // Check if we're switching between portrait and landscape orientations
                            bool currentIsPortrait = currentOrientation == 1 || currentOrientation == 3; // 90 or 270 degrees
                            bool newIsPortrait = orientation == 1 || orientation == 3; // 90 or 270 degrees
                            
                            // If orientation type is changing (portrait to landscape or vice versa), swap dimensions
                            if (currentIsPortrait != newIsPortrait)
                            {
                                int temp = width;
                                width = height;
                                height = temp;
                            }
                        }
                    }
                }

                await _displayApi.SetDisplaySettingsAsync(monitorId, width, height, frequency, orientation, noPersist);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling display settings change: {ex.Message}");
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Log("Disposing Display Feature Handler");
                _displayApi.StateChanged -= OnDisplayStateChanged;
                _displayApi.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

