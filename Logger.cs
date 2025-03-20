using System.Text;
using System.IO;
using System.Diagnostics;

namespace WinControlCenter
{
    public static class Logger
    {
        private static readonly string LogFile;
        private static readonly string FallbackLogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinControlCenter",
            "debug.log");
        private static readonly object LockObject = new();

        static Logger()
        {
            try
            {
                // Get the actual executable path
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    throw new Exception("Could not get executable path");
                }
                LogFile = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "debug.log");
                
                // Try to create log file in executable directory first
                File.WriteAllText(LogFile, $"=== Log started at {DateTime.Now} ===\n");
                Debug.WriteLine($"Log file initialized at: {LogFile}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize log file in executable directory: {ex.Message}");
                // Try to log to LocalApplicationData as fallback
                try
                {
                    // Ensure the directory exists
                    var logDir = Path.GetDirectoryName(FallbackLogFile);
                    if (!string.IsNullOrEmpty(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    File.WriteAllText(FallbackLogFile, $"=== Log started at {DateTime.Now} ===\n");
                    Debug.WriteLine($"Fallback log file initialized at: {FallbackLogFile}");
                }
                catch (Exception fallbackEx)
                {
                    Debug.WriteLine($"Failed to initialize fallback log file: {fallbackEx.Message}");
                }
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (LockObject)
                {
                    var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
                    // Try to write to executable directory first
                    File.AppendAllText(LogFile, logMessage, Encoding.UTF8);
                    Debug.WriteLine(message); // Also write to debug output
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to log file in executable directory: {ex.Message}");
                // Try to write to LocalApplicationData as fallback
                try
                {
                    var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
                    File.AppendAllText(FallbackLogFile, logMessage, Encoding.UTF8);
                }
                catch (Exception fallbackEx)
                {
                    Debug.WriteLine($"Failed to write to fallback log file: {fallbackEx.Message}");
                }
            }
        }
    }
} 