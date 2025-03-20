using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace WinControlCenter
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _healthCheckTimer;
        private bool _isWebViewInitialized;
        private bool _isWebViewLoading;
        private readonly SemaphoreSlim _initializationLock = new(1, 1);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        public MainWindow()
        {
            _healthCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _healthCheckTimer.Tick += HealthCheckTimer_Tick;

            try
            {
                InitializeComponent();
                InitializeHealthCheck();
                SetupWindowBehavior();
                Logger.Log("MainWindow initialized successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize application: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.Log($"Initialization error: {ex}");
                Application.Current.Shutdown();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _ = InitializeWebViewAsync();
        }

        private void InitializeHealthCheck()
        {
            _healthCheckTimer.Start();
        }

        private void SetupWindowBehavior()
        {
            Deactivated += MainWindow_Deactivated;
            Closing += MainWindow_Closing;
        }

        private async Task InitializeWebViewAsync()
        {
            // Prevent multiple simultaneous initialization attempts
            if (_isWebViewLoading || _isWebViewInitialized) return;

            try
            {
                await _initializationLock.WaitAsync();
                if (_isWebViewLoading || _isWebViewInitialized) return;
                _isWebViewLoading = true;

                UpdateStatus("Initializing WebView...");

                // Use the shared WebServer instance
                var webServer = App.SharedWebServer;

                if (webServer != null && WebViewControl != null)
                {
                    // Initialize WebView2 in a background task
                    await Task.Run(async () =>
                    {
                        try
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                // Update the URL format to use the password as the first path segment
                                var url = $"http://localhost:{webServer.Port}/{webServer.AccessPassword}";
                                WebViewControl.Source = new Uri(url);
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error setting WebView source: {ex.Message}");
                            throw;
                        }
                    });

                    _isWebViewInitialized = true;
                    Logger.Log($"WebView initialized successfully on port {webServer.Port}");
                    UpdateStatus($"External Port: {webServer?.Port} | Password: {webServer?.AccessPassword}");
                }
                else
                {
                    Logger.Log("Warning: WebViewControl or WebServer is null");
                    UpdateStatus("Error: WebView not initialized");
                    Hide();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize WebView: {ex}");
                UpdateStatus($"WebView Error: {ex.Message}");
                Hide();
            }
            finally
            {
                _isWebViewLoading = false;
                _initializationLock.Release();
            }
        }

        private void UpdateStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.Text = message;
                StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                Logger.Log($"Warning: StatusText control not initialized. Message: {message}");
            }
        }

        private void WebViewControl_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Logger.Log($"CoreWebView2 initialization failed: {e.InitializationException}");
                UpdateStatus("Error: WebView initialization failed");
                WebViewControl.Visibility = Visibility.Collapsed;
                return;
            }

            if (WebViewControl?.CoreWebView2 != null)
            {
                Logger.Log("CoreWebView2 initialized successfully");
                ConfigureWebView2Settings(WebViewControl.CoreWebView2);
                WebViewControl.Visibility = Visibility.Visible; // Show WebView only after successful initialization
            }
        }

        private static void ConfigureWebView2Settings(CoreWebView2 coreWebView2)
        {
            coreWebView2.Settings.IsScriptEnabled = true;
            coreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            coreWebView2.Settings.IsWebMessageEnabled = true;
            coreWebView2.Settings.IsStatusBarEnabled = false;
            coreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            coreWebView2.Settings.IsZoomControlEnabled = false;
            coreWebView2.Settings.IsBuiltInErrorPageEnabled = true;
        }

        private void WebViewControl_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Logger.Log($"Navigation starting: {e.Uri}");
            UpdateStatus("Loading...");
        }

        private void WebViewControl_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var webServer = App.SharedWebServer;
            if (e.IsSuccess)
            {
                Logger.Log($"Navigation completed: {e.WebErrorStatus}");
                UpdateStatus($"External Port: {webServer?.Port} | Password: {webServer?.AccessPassword}");
                WebViewControl.Visibility = Visibility.Visible;
            }
            else
            {
                Logger.Log($"Navigation failed: {e.WebErrorStatus}");
                UpdateStatus($"Error: {e.WebErrorStatus}");
                WebViewControl.Visibility = Visibility.Collapsed;
            }
        }

        private void WebViewControl_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                Logger.Log($"Web message received: {e.WebMessageAsJson}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling web message: {ex}");
            }
        }

        private async void HealthCheckTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // Check if WebView and WebServer are available
                if (!_isWebViewInitialized && !_isWebViewLoading && WebViewControl != null && App.SharedWebServer != null)
                {
                    var webServer = App.SharedWebServer;
                    Logger.Log($"Attempting to reinitialize WebView on port {webServer.Port}...");
                    await InitializeWebViewAsync();
                }
                else if (App.SharedWebServer == null)
                {
                    Logger.Log("WebServer is not initialized");
                    UpdateStatus("Error: WebServer not initialized");
                    _healthCheckTimer.Stop(); // Stop timer if WebServer is not available
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in health check: {ex.Message}");
                UpdateStatus("Error during health check");
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _healthCheckTimer.Stop();
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            if (!IsForegroundWindowInApp())
            {
                Hide();
            }
        }

        private bool IsForegroundWindowInApp()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;

                GetWindowThreadProcessId(hwnd, out int processId);
                return processId == Process.GetCurrentProcess().Id;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking foreground window: {ex}");
                return false;
            }
        }

        public new void Show()
        {
            if (!_isWebViewInitialized)
            {
                UpdateStatus("Please wait, initializing...");
                WebViewControl.Visibility = Visibility.Collapsed;
            }
            base.Show();
        }

        private async Task RefreshWebViewOnly()
        {
            try
            {
                var webServer = App.SharedWebServer;
                if (webServer == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (WebViewControl.CoreWebView2 != null)
                        {
                            var url = $"http://localhost:{webServer.Port}/{webServer.AccessPassword}";
                            WebViewControl.Source = new Uri(url);

                            // Wait for navigation to complete
                            var tcs = new TaskCompletionSource<bool>();
                            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) 
                            {
                                WebViewControl.NavigationCompleted -= Handler;
                                tcs.SetResult(e.IsSuccess);
                            }
                            WebViewControl.NavigationCompleted += Handler;

                            await tcs.Task;
                            UpdateStatus($"External Port: {webServer.Port} | Password: {webServer.AccessPassword}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error refreshing WebView: {ex.Message}");
                        UpdateStatus("Error refreshing WebView");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in RefreshWebViewOnly: {ex.Message}");
                UpdateStatus("Error refreshing view");
            }
        }

        public async Task RestartServerAndRefreshWebView(bool requiresServerRestart = false)
        {
            try
            {
                var webServer = App.SharedWebServer;
                if (webServer == null) return;

                if (requiresServerRestart)
                {
                    UpdateStatus("Restarting server...");

                    // Stop the server
                    await webServer.StopWebServerAsync();
                    await Task.Delay(500);

                    // Start the server again
                    await webServer.StartAsync();
                    await Task.Delay(500);

                    // Reset WebView initialization flag
                    _isWebViewInitialized = false;
                    _isWebViewLoading = false;

                    // Reinitialize WebView
                    await InitializeWebViewAsync();
                }
                else
                {
                    // Just refresh the WebView without server restart
                    await RefreshWebViewOnly();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error restarting server and refreshing WebView: {ex.Message}");
                UpdateStatus("Error refreshing connection");
            }
        }
    }
}
