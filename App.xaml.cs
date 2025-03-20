using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Threading;

namespace WinControlCenter;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    // Use a static instance to ensure only one WebServer exists application-wide
    public static WebServer? SharedWebServer { get; private set; }
    private TaskbarIcon? _trayIcon;
    private bool _isShuttingDown;
    private SettingWindow? _settingWindow;
    private static Mutex? _singleInstanceMutex;
    private bool _isWebServerInitialized;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Check for existing instance
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, "WinControlCenterSingleInstanceMutex", out createdNew);
        
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Warning:\nAn instance of WinControlCenter is already running.\n\nUse the current tray icon to manage your applications.", "WinControlCenter", MessageBoxButton.OK, MessageBoxImage.Warning);
            _singleInstanceMutex = null;
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        
        try
        {
            // Start WebServer initialization in parallel
            var webServerTask = Task.Run(async () =>
            {
                SharedWebServer = new WebServer(developmentMode: true);
                await SharedWebServer.StartWebServer();
                _isWebServerInitialized = true;
            });
            
            // Initialize tray icon while WebServer is starting
            InitializeTrayIcon();
            
            // Create MainWindow but don't show it yet
            MainWindow = new MainWindow();
            
            // Wait for WebServer to complete initialization
            await webServerTask;
            
            // Hide MainWindow initially
            MainWindow.Hide();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to initialize application: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = (TaskbarIcon)FindResource("WinControlCenterTrayIcon");
        _trayIcon.TrayMouseDoubleClick += TaskbarIcon_TrayMouseDoubleClick;

        if (_trayIcon.ContextMenu?.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(mi => mi.Name == "ExitMenuItem") is System.Windows.Controls.MenuItem exitMenuItem)
        {
            exitMenuItem.Click += Exit_Click;
        }
        if (_trayIcon?.ContextMenu?.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(mi => mi.Name == "SettingMenuItem") is System.Windows.Controls.MenuItem settingMenuItem)
        {
            settingMenuItem.Click += SettingMenuItem_Click;
        }
    }

    private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        if (MainWindow == null) return;

        var dpi = VisualTreeHelper.GetDpi(MainWindow);
        var mousePos = System.Windows.Forms.Control.MousePosition;
        var (mouseX, mouseY) = (mousePos.X / dpi.DpiScaleX, mousePos.Y / dpi.DpiScaleY);
        
        var screen = Screen.FromPoint(new System.Drawing.Point((int)mouseX, (int)mouseY));
        var workingArea = screen.WorkingArea;
        var (dpiRight, dpiBottom) = (workingArea.Right / dpi.DpiScaleX, workingArea.Bottom / dpi.DpiScaleY);

        MainWindow.Left = Math.Min(mouseX - MainWindow.Width, dpiRight - MainWindow.Width);
        MainWindow.Top = Math.Min(dpiBottom - MainWindow.Height, dpiBottom - MainWindow.Height);

        MainWindow.Show();
        MainWindow.Activate();
    }

    private void SettingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_isWebServerInitialized || SharedWebServer == null)
        {
            /*System.Windows.MessageBox.Show("Please wait for the application to finish initializing.", 
                "Initialization in Progress", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);*/
            return;
        }

        if (_settingWindow == null || !_settingWindow.IsVisible)
        {
            _settingWindow = new SettingWindow(SharedWebServer);
            _settingWindow.Show();
        }
        else
        {
            _settingWindow.Activate();
        }
    }

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_isShuttingDown) return;

        try
        {
            Logger.Log("Starting application shutdown sequence...");
            _isShuttingDown = true;

            // Disable the exit menu item to prevent multiple clicks
            if (sender is System.Windows.Controls.MenuItem menuItem)
            {
                menuItem.IsEnabled = false;
                Logger.Log("Exit menu item disabled");
            }

            // Hide the main window if it's visible
            if (MainWindow?.IsVisible == true)
            {
                Logger.Log("Hiding main window");
                MainWindow.Hide();
            }

            // Stop the WebServer asynchronously and wait for it to complete
            Logger.Log("Stopping WebServer...");
            await SharedWebServer.StopWebServerAsync();
            Logger.Log("WebServer stopped successfully");
            
            // Give a short delay to ensure all resources are cleaned up
            Logger.Log("Waiting for final cleanup...");
            await Task.Delay(100);
            
            Logger.Log("Disposing tray icon...");
            _trayIcon?.Dispose();
            
            Logger.Log("Initiating application shutdown...");
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Log($"Critical error during application shutdown: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            System.Windows.MessageBox.Show($"Error during shutdown: {ex.Message}", "Shutdown Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            _isShuttingDown = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Log("OnExit called - performing final cleanup");
        base.OnExit(e);
        // Only dispose the tray icon here, as WebServer is already stopped in Exit_Click
        _trayIcon?.Dispose();
        // Release the mutex
        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
        Logger.Log("Application shutdown complete");
    }
}



