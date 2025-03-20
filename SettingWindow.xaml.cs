using System;
using System.Windows;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Controls;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using Microsoft.Win32;

namespace WinControlCenter
{
    public class BooleanToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool isEnabled && isEnabled) ? Brushes.Black : Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NetworkInterfaceModel : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private readonly WebServer _webServer;
        public string IpAddress { get; }
        public string SubnetMask { get; }
        public string NetworkAddress { get; }
        public NetworkInterface NetworkInterface { get; }
        public ObservableCollection<string> Urls { get; } = new();

        public NetworkInterfaceModel(WebServer webServer, string ipAddress, string subnetMask, string networkAddress, NetworkInterface networkInterface)
        {
            _webServer = webServer;
            IpAddress = ipAddress;
            SubnetMask = subnetMask;
            NetworkAddress = networkAddress;
            NetworkInterface = networkInterface;
            _isEnabled = webServer.IsSubnetEnabled(networkAddress, subnetMask);
            UpdateUrls();
        }

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
                        _webServer.AddAllowedSubnet(NetworkAddress, SubnetMask);
                    }
                    else
                    {
                        _webServer.RemoveAllowedSubnet(NetworkAddress, SubnetMask);
                    }
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                }
            }
        }

        public void UpdateUrls()
        {
            Urls.Clear();
            Urls.Add($"http://{IpAddress}:{_webServer.Port}/{_webServer.AccessPassword}");

            // Add IPv6 addresses if available
            var ipv6Addresses = NetworkInterface.GetIPProperties().UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                           !ua.Address.IsIPv6LinkLocal);

            foreach (var ipv6 in ipv6Addresses)
            {
                Urls.Add($"http://[{ipv6.Address}]:{_webServer.Port}/{_webServer.AccessPassword}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Interaction logic for SettingWindow.xaml
    /// </summary>
    public partial class SettingWindow : Window
    {
        private readonly WebServer _webServer;
        private static readonly Regex PasswordRegex = new("^(?=.*[a-z])(?=.*[A-Z])[a-zA-Z0-9]{8}$");
        private readonly ObservableCollection<string> _urls = new();
        private readonly ObservableCollection<NetworkInterfaceModel> _networks = new();
        private bool _isInitializing = true;
        private bool _isClosing = false;

        public SettingWindow(WebServer webServer)
        {
            if (webServer == null)
            {
                throw new ArgumentNullException(nameof(webServer), "WebServer instance cannot be null");
            }

            InitializeComponent();
            _webServer = webServer;
            InitializeSettings();
        }

        private void InitializeSettings()
        {
            try
            {
                UpdatePasswordDisplay();
                InitializeFeatureStates();
                
                // Initialize network interfaces and URL lists
                NetworkList.ItemsSource = _networks;
                UrlList.ItemsSource = _urls;
                InitializeNetworkInterfaces();
                UpdateConnectionUrls();

                // Initialize password field
                if (!string.IsNullOrEmpty(_webServer.AccessPassword))
                {
                    PasswordTextBox.Text = _webServer.AccessPassword;
                }

                // Initialize mouse sensitivity settings
                var mouseState = _webServer.GetMouseState();
                if (mouseState != null)
                {
                    MouseSensitivitySlider.Value = mouseState.Sensitivity;
                    ScrollSensitivitySlider.Value = mouseState.ScrollSensitivity;
                    AccelerationFactorSlider.Value = mouseState.AccelerationFactor;
                    MaxAccelerationSlider.Value = mouseState.MaxAcceleration;
                }
                else
                {
                    // Set default values from MouseDefaults
                    MouseSensitivitySlider.Value = MouseDefaults.Sensitivity;
                    ScrollSensitivitySlider.Value = MouseDefaults.ScrollSensitivity;
                    AccelerationFactorSlider.Value = MouseDefaults.AccelerationFactor;
                    MaxAccelerationSlider.Value = MouseDefaults.MaxAcceleration;
                }

                _isInitializing = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize settings: {ex.Message}\nPlease try again in a moment.", 
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _isClosing = true;
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
            }
        }

        private void InitializeNetworkInterfaces()
        {
            _networks.Clear();
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            foreach (var info in _webServer.GetLocalNetworkInfo())
            {
                var ni = interfaces.FirstOrDefault(ni => ni.GetIPProperties().UnicastAddresses
                    .Any(ua => ua.Address.ToString() == info.IpAddress));

                if (ni != null)
                {
                    var network = new NetworkInterfaceModel(
                        _webServer,
                        info.IpAddress,
                        info.SubnetMask,
                        info.NetworkAddress,
                        ni
                    );
                    _networks.Add(network);
                }
            }
        }

        private void UpdateConnectionUrls()
        {
            try
            {
                _urls.Clear();
                // Add localhost variants
                _urls.Add($"http://localhost:{_webServer.Port}/{_webServer.AccessPassword}");
                _urls.Add($"http://127.0.0.1:{_webServer.Port}/{_webServer.AccessPassword}");
                _urls.Add($"http://[::1]:{_webServer.Port}/{_webServer.AccessPassword}");

                // Update network URLs
                foreach (var network in _networks)
                {
                    network.UpdateUrls();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating connection URLs: {ex.Message}");
            }
        }

        private void InitializeFeatureStates()
        {
            // Set initial checkbox states based on WebServer state
            EnableSpeakerControl.IsChecked = _webServer.IsSpeakerEnabled;
            EnableMicrophoneControl.IsChecked = _webServer.IsMicrophoneEnabled;
            EnableMouseControl.IsChecked = _webServer.IsMouseEnabled;
            EnableDisplayControl.IsChecked = _webServer.IsDisplayEnabled;
            
            // Initialize startup setting
            RunAtStartup.IsChecked = _webServer.RunAtStartup;
        }

        private void RunAtStartup_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            if (sender is CheckBox checkBox)
            {
                try
                {
                    _webServer.RunAtStartup = checkBox.IsChecked ?? false;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to update startup setting: {ex.Message}", 
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    // Revert checkbox state to actual state
                    checkBox.IsChecked = _webServer.RunAtStartup;
                }
            }
        }

        private async void AudioFeature_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            try
            {
                if (sender is CheckBox checkBox)
                {
                    string featureType = "";
                    bool isEnabled = checkBox.IsChecked ?? false;

                    switch (checkBox.Name)
                    {
                        case "EnableSpeakerControl":
                            _webServer.IsSpeakerEnabled = isEnabled;
                            featureType = "speaker";
                            break;
                        case "EnableMicrophoneControl":
                            _webServer.IsMicrophoneEnabled = isEnabled;
                            featureType = "microphone";
                            break;
                        case "EnableMouseControl":
                            _webServer.IsMouseEnabled = isEnabled;
                            featureType = "mouse";
                            break;
                        case "EnableDisplayControl":
                            _webServer.IsDisplayEnabled = isEnabled;
                            featureType = "display";
                            break;
                    }

                    if (!string.IsNullOrEmpty(featureType))
                    {
                        // Single broadcast for feature state change
                        await _webServer.BroadcastMessageAsync(new { 
                            type = "error", 
                            message = $"{featureType} {(isEnabled ? "enabled" : "disabled")}" 
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update feature state: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdatePasswordDisplay()
        {
            PasswordTextBox.Text = _webServer.AccessPassword;
        }

        private async void SetPassword_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var password = PasswordTextBox.Text;
                if (!Regex.IsMatch(password, "^(?=.*[a-z])(?=.*[A-Z])[a-zA-Z0-9]{8}$"))
                {
                    MessageBox.Show("Password must be exactly 8 characters long, contain at least one lowercase letter, one uppercase letter, and only letters and numbers.", 
                        "Invalid Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _webServer.SetPassword(password);
                UpdateUrlList();

                // Restart server and refresh WebView
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    await mainWindow.RestartServerAndRefreshWebView();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set password: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RegeneratePassword_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _webServer.RegeneratePassword();
                PasswordTextBox.Text = _webServer.AccessPassword;
                UpdateUrlList();

                // Restart server and refresh WebView
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    await mainWindow.RestartServerAndRefreshWebView();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to regenerate password: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void UpdateMouseSettings()
        {
            if (_isInitializing) return;

            try
            {
                _webServer.UpdateMouseSettings(
                    MouseSensitivitySlider.Value,
                    ScrollSensitivitySlider.Value,
                    AccelerationFactorSlider.Value,
                    MaxAccelerationSlider.Value
                );

                // Refresh WebView after mouse settings update
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    await mainWindow.RestartServerAndRefreshWebView();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating mouse settings: {ex.Message}");
            }
        }

        private void MouseSensitivity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateMouseSettings();
        }

        private void ScrollSensitivity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateMouseSettings();
        }

        private void AccelerationFactor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateMouseSettings();
        }

        private void MaxAcceleration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateMouseSettings();
        }

        private void ResetMouseSensitivity_Click(object sender, RoutedEventArgs e)
        {
            MouseSensitivitySlider.Value = MouseDefaults.Sensitivity;
        }

        private void ResetScrollSensitivity_Click(object sender, RoutedEventArgs e)
        {
            ScrollSensitivitySlider.Value = MouseDefaults.ScrollSensitivity;
        }

        private void ResetAccelerationFactor_Click(object sender, RoutedEventArgs e)
        {
            AccelerationFactorSlider.Value = MouseDefaults.AccelerationFactor;
        }

        private void ResetMaxAcceleration_Click(object sender, RoutedEventArgs e)
        {
            MaxAccelerationSlider.Value = MouseDefaults.MaxAcceleration;
        }

        private void UpdateUrlList()
        {
            try
            {
                // Force UI refresh
                if (UrlList.ItemsSource == null)
                {
                    UrlList.ItemsSource = _urls;
                }
                else
                {
                    // Force the ItemsControl to refresh
                    UrlList.Items.Refresh();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating URL list: {ex.Message}");
            }
        }

        private async void NetworkInterface_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            try
            {
                if (sender is CheckBox checkBox && checkBox.DataContext is NetworkInterfaceModel network)
                {
                    network.IsEnabled = checkBox.IsChecked ?? false;

                    // Restart server to apply changes
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        await mainWindow.RestartServerAndRefreshWebView();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update network interface state: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
