using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

//Only contains the Webserver and WebSocket common feature handlers
namespace WinControlCenter
{
    public interface ISystemApi : IDisposable
    {
        event EventHandler<object> StateChanged;
        Task<object> GetStateAsync();
    }

    public abstract class BaseFeatureHandler : IDisposable
    {
        protected readonly WebServer WebServer;
        public string FeatureType { get; }

        protected BaseFeatureHandler(WebServer webServer, string featureType)
        {
            WebServer = webServer;
            FeatureType = featureType;
        }

        public async Task BroadcastStateAsync()
        {
            try
            {
                var state = await GetStateAsync();
                await WebServer.BroadcastMessageAsync(new { type = FeatureType, value = state });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error broadcasting state: {ex.Message}");
                await WebServer.BroadcastMessageAsync(new { type = "error", message = $"Failed to get {FeatureType} state" });
            }
        }

        protected abstract Task<object> GetStateAsync();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    public abstract class WebSocketFeatureHandler : BaseFeatureHandler
    {
        protected virtual string GetStateMessageType => $"get{FeatureType}";
        protected virtual string SetStateMessageType => FeatureType;

        protected WebSocketFeatureHandler(WebServer webServer, string featureType)
            : base(webServer, featureType)
        {
        }

        public virtual async Task HandleMessageAsync(JsonElement data)
        {
            var messageType = data.GetProperty("Type").GetString();

            if (messageType == GetStateMessageType)
            {
                await BroadcastStateAsync();
            }
            else if (messageType == SetStateMessageType && data.TryGetProperty("Data", out var dataElement))
            {
                await HandleSetStateAsync(dataElement);
            }
        }

        protected abstract Task HandleSetStateAsync(JsonElement valueElement);
    }

    public class WebSocketRequest
    {
        public required string Type { get; set; }
        public JsonElement Data { get; set; }
    }

    /// <summary>
    /// Simple rate limiter implementation
    /// </summary>
    public class RateLimiter
    {
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestHistory = new();
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly object _lock = new();

        public RateLimiter(int maxRequests, TimeSpan timeWindow)
        {
            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
        }

        public bool TryAcquire(string clientId)
        {
            var now = DateTime.UtcNow;
            var history = _requestHistory.GetOrAdd(clientId, _ => new Queue<DateTime>());

            lock (_lock)
            {
                while (history.Count > 0 && now - history.Peek() > _timeWindow)
                {
                    history.Dequeue();
                }

                if (history.Count >= _maxRequests) return false;

                history.Enqueue(now);
                return true;
            }
        }

        public void ClearHistory(string clientId) => _requestHistory.TryRemove(clientId, out _);
    }

    public record NetworkInfo(string IpAddress, string SubnetMask, string NetworkAddress);

    /// <summary>
    /// Web server for the control center
    /// </summary>
    public class WebServer
    {
        private readonly int _port;
        private readonly ISystemApi _speakerApi;
        private readonly ISystemApi _microphoneApi;
        private readonly ISystemApi _mouseApi;
        private readonly ISystemApi _displayApi;
        private readonly ConcurrentDictionary<string, WebSocket> _connectedClients = new();
        private readonly HashSet<string> _authenticatedClients = new();
        private readonly List<WebSocketFeatureHandler> _featureHandlers = new();
        private bool _isRunning;
        private readonly bool _developmentMode;
        private WebApplication? _app;
        private string _accessPassword;
        private const string SettingsFile = "WinControlCenter.Settings.xml";
        private readonly HashSet<NetworkSubnet> _enabledSubnets = new();

        private readonly RateLimiter _rateLimiter;
        private readonly SemaphoreSlim _broadcastSemaphore = new(1, 1);

        private const int MaxPortAttempts = 99;
        private const int StartPort = 8080;
        private const string HtmlResource = "WinControlCenter.Main.html";
        private const int MaxMessageSize = 4096;
        private const int RateLimitRequests = 100;
        private const int RateLimitWindowSeconds = 1;

        private bool _enableLanControl = true;
        public bool EnableLanControl
        {
            get => _enableLanControl;
            set
            {
                _enableLanControl = value;
                SaveSettings();
            }
        }

        private bool _runAtStartup;
        public bool RunAtStartup
        {
            get => _runAtStartup;
            set
            {
                if (_runAtStartup != value)
                {
                    _runAtStartup = value;
                    UpdateStartupRegistry(value);  // Update registry first
                    SaveSettings();  // Then save to XML
                }
            }
        }

        public int Port => _port;
        public string AccessPassword => _accessPassword;

        public bool IsSpeakerEnabled
        {
            get => (_speakerApi as IAudioApi)?.IsEnabled ?? false;
            set
            {
                if (_speakerApi is IAudioApi api)
                {
                    api.IsEnabled = value;
                    SaveSettings();
                }
            }
        }

        public bool IsMicrophoneEnabled
        {
            get => (_microphoneApi as IAudioApi)?.IsEnabled ?? false;
            set
            {
                if (_microphoneApi is IAudioApi api)
                {
                    api.IsEnabled = value;
                    SaveSettings();
                }
            }
        }

        public bool IsMouseEnabled
        {
            get => (_mouseApi as IMouseApi)?.IsEnabled ?? false;
            set
            {
                if (_mouseApi is IMouseApi api)
                {
                    api.IsEnabled = value;
                    SaveSettings();
                }
            }
        }

        public bool IsDisplayEnabled
        {
            get => (_displayApi as SetResolution.IDisplayApi)?.IsEnabled ?? false;
            set
            {
                if (_displayApi is SetResolution.IDisplayApi api)
                {
                    api.IsEnabled = value;
                    SaveSettings();
                }
            }
        }

        public WebServer(bool developmentMode = false)
        {
            _developmentMode = false;
            //_developmentMode = developmentMode;
            _port = FindAvailablePort(StartPort);
            FirewallHelper.EnsureFirewallRule(_port);

            _speakerApi = AudioFactory.CreateSpeakerApi();
            _microphoneApi = AudioFactory.CreateMicrophoneApi();
            _mouseApi = MouseFactory.CreateMouseApi();
            _displayApi = SetResolution.DisplayFactory.CreateDisplayApi();
            _rateLimiter = new RateLimiter(RateLimitRequests, TimeSpan.FromSeconds(RateLimitWindowSeconds));

            if (IsSpeakerEnabled)
                _featureHandlers.Add(new AudioFeatureHandler(this, (IAudioApi)_speakerApi, "speaker"));
            if (IsMicrophoneEnabled)
                _featureHandlers.Add(new AudioFeatureHandler(this, (IAudioApi)_microphoneApi, "microphone"));
            if (IsMouseEnabled)
                _featureHandlers.Add(new MouseFeatureHandler(this, (IMouseApi)_mouseApi));
            if (IsDisplayEnabled)
                _featureHandlers.Add(new SetResolution.DisplayFeatureHandler(this, (SetResolution.IDisplayApi)_displayApi));

            LoadOrGeneratePassword();

            Logger.Log($"WebServer initialized on port {_port}, Development Mode: {_developmentMode}");
        }

        public record NetworkSubnet(string Network, string Mask)
        {
            public bool IsMatch(IPAddress ip)
            {
                if (!IPAddress.TryParse(Network, out var networkIp) || 
                    !IPAddress.TryParse(Mask, out var maskIp))
                    return false;

                var ipBytes = ip.GetAddressBytes();
                var networkBytes = networkIp.GetAddressBytes();
                var maskBytes = maskIp.GetAddressBytes();

                // Handle different IP versions
                if (ipBytes.Length != networkBytes.Length)
                    return false;

                for (int i = 0; i < ipBytes.Length; i++)
                {
                    if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                        return false;
                }
                return true;
            }
        }

        private void LoadOrGeneratePassword()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var doc = XDocument.Load(SettingsFile);
                    var passwordElement = doc.Root?.Element("Password");
                    if (passwordElement != null)
                    {
                        _accessPassword = passwordElement.Value;
                    }

                    // Load settings.xml
                    var featuresElement = doc.Root?.Element("Features");
                    if (featuresElement != null)
                    {
                        var speakerEnabled = bool.Parse(featuresElement.Element("Speaker")?.Value ?? "true");
                        var microphoneEnabled = bool.Parse(featuresElement.Element("Microphone")?.Value ?? "true");
                        var mouseEnabled = bool.Parse(featuresElement.Element("Mouse")?.Value ?? "true");
                        var displayEnabled = bool.Parse(featuresElement.Element("Display")?.Value ?? "true");
                        var runAtStartup = bool.Parse(featuresElement.Element("RunAtStartup")?.Value ?? "false");

                        if (_speakerApi is IAudioApi speakerApi)
                            speakerApi.IsEnabled = speakerEnabled;
                        if (_microphoneApi is IAudioApi microphoneApi)
                            microphoneApi.IsEnabled = microphoneEnabled;
                        if (_mouseApi is IMouseApi mouseApi)
                            mouseApi.IsEnabled = mouseEnabled;
                        if (_displayApi is SetResolution.IDisplayApi displayApi)
                            displayApi.IsEnabled = displayEnabled;
                        
                        _runAtStartup = runAtStartup;
                        UpdateStartupRegistry(runAtStartup);

                        // Load enabled subnets
                        var subnetsElement = featuresElement.Element("EnabledSubnets");
                        if (subnetsElement != null)
                        {
                            _enabledSubnets.Clear();
                            foreach (var subnetElement in subnetsElement.Elements("Subnet"))
                            {
                                var network = subnetElement.Element("Network")?.Value;
                                var mask = subnetElement.Element("Mask")?.Value;
                                if (network != null && mask != null)
                                {
                                    _enabledSubnets.Add(new NetworkSubnet(network, mask));
                                }
                            }
                        }

                        // Load mouse settings
                        var mouseSettingsElement = featuresElement.Element("MouseSettings");
                        if (mouseSettingsElement != null && _mouseApi is MouseKeyHookManager mouseManager)
                        {
                            var sensitivity = double.Parse(mouseSettingsElement.Element("Sensitivity")?.Value ?? MouseDefaults.Sensitivity.ToString());
                            var scrollSensitivity = double.Parse(mouseSettingsElement.Element("ScrollSensitivity")?.Value ?? MouseDefaults.ScrollSensitivity.ToString());
                            var accelerationFactor = double.Parse(mouseSettingsElement.Element("AccelerationFactor")?.Value ?? MouseDefaults.AccelerationFactor.ToString());
                            var maxAcceleration = double.Parse(mouseSettingsElement.Element("MaxAcceleration")?.Value ?? MouseDefaults.MaxAcceleration.ToString());

                            mouseManager.UpdateSensitivitySettings(
                                sensitivity,
                                scrollSensitivity,
                                accelerationFactor,
                                maxAcceleration
                            );
                        }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading settings: {ex.Message}");
            }

            RegeneratePassword();
            // Do not initialize any subnets by default
            _enabledSubnets.Clear();
            SaveSettings();
        }

        private void InitializeDefaultSubnets()
        {
            _enabledSubnets.Clear();
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                var mouseSettings = _mouseApi is MouseKeyHookManager mouseManager
                    ? (MouseState)mouseManager.GetStateAsync().Result
                    : null;

                var doc = new XDocument(
                    new XElement("Settings",
                        new XElement("Password", _accessPassword),
                        new XElement("Features",
                            new XElement("Speaker", IsSpeakerEnabled.ToString()),
                            new XElement("Microphone", IsMicrophoneEnabled.ToString()),
                            new XElement("Mouse", IsMouseEnabled.ToString()),
                            new XElement("Display", IsDisplayEnabled.ToString()),
                            new XElement("RunAtStartup", RunAtStartup.ToString()),
                            new XElement("EnabledSubnets",
                                _enabledSubnets.Select(subnet =>
                                    new XElement("Subnet",
                                        new XElement("Network", subnet.Network),
                                        new XElement("Mask", subnet.Mask)
                                    )
                                )
                            ),
                            new XElement("MouseSettings",
                                new XElement("Sensitivity", mouseSettings?.Sensitivity ?? MouseDefaults.Sensitivity),
                                new XElement("ScrollSensitivity", mouseSettings?.ScrollSensitivity ?? MouseDefaults.ScrollSensitivity),
                                new XElement("AccelerationFactor", mouseSettings?.AccelerationFactor ?? MouseDefaults.AccelerationFactor),
                                new XElement("MaxAcceleration", mouseSettings?.MaxAcceleration ?? MouseDefaults.MaxAcceleration)
                            )
                        )
                    )
                );

                // Create a backup of the existing file
                if (File.Exists(SettingsFile))
                {
                    File.Copy(SettingsFile, SettingsFile + ".bak", true);
                }

                // Save the new settings
                doc.Save(SettingsFile);

                Logger.Log("Settings saved successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error saving settings: {ex.Message}");
                // Try to restore from backup if save failed
                if (File.Exists(SettingsFile + ".bak"))
                {
                    try
                    {
                        File.Copy(SettingsFile + ".bak", SettingsFile, true);
                    }
                    catch (Exception backupEx)
                    {
                        Logger.Log($"Error restoring settings backup: {backupEx.Message}");
                    }
                }
            }
        }

        private int FindAvailablePort(int startPort)
        {
            for (int port = startPort; port <= startPort + MaxPortAttempts; port++)
            {
                try
                {
                    using var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch { continue; }
            }
            throw new Exception($"No available ports found between {startPort} and {startPort + MaxPortAttempts}");
        }

        public IEnumerable<NetworkInfo> GetLocalNetworkInfo()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses
                    .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip =>
                    {
                        var ipBytes = ip.Address.GetAddressBytes();
                        var maskBytes = ip.IPv4Mask.GetAddressBytes();
                        var networkBytes = ipBytes.Zip(maskBytes, (i, m) => (byte)(i & m)).ToArray();

                        return new NetworkInfo(
                            ip.Address.ToString(),
                            ip.IPv4Mask.ToString(),
                            string.Join(".", networkBytes)
                        );
                    }));
        }

        private void LogConnectionUrls()
        {
            Logger.Log("\nAvailable connection URLs:");
            Logger.Log($"Local: http://localhost:{_port}/{_accessPassword}");

            foreach (var info in GetLocalNetworkInfo())
            {
                Logger.Log($"Network: http://{info.IpAddress}:{_port}/{_accessPassword}");
            }
        }

        public async Task StartWebServer()
        {
            if (_isRunning) return;

            var builder = WebApplication.CreateBuilder();

            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Any, _port);  // IPv4
                options.Listen(IPAddress.IPv6Any, _port);  // IPv6
                options.Listen(IPAddress.Loopback, _port);  // IPv4 localhost
                options.Listen(IPAddress.IPv6Loopback, _port);  // IPv6 localhost
            });

            builder.Services.Configure<WebSocketOptions>(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(120);
            });

            _app = builder.Build();
            _app.UseWebSockets();

            _app.Map("/{**path}", async (HttpContext context) =>
            {
                var path = context.Request.Path.Value?.TrimStart('/').TrimEnd('/') ?? "";
                var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var password = segments.FirstOrDefault() ?? "";
                var remainingPath = string.Join("/", segments.Skip(1));

                // Check password first, before any other checks
                if (!_developmentMode && (string.IsNullOrEmpty(password) || password != _accessPassword))
                {
                    Logger.Log($"Authorization failed - Password mismatch or empty. Provided: '{password}'");
                    context.Abort();
                    return;
                }

                if (!_rateLimiter.TryAcquire(clientIp))
                {
                    Logger.Log($"Rate limit exceeded for client {clientIp}");
                    context.Abort();
                    return;
                }

                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var clientId = Guid.NewGuid().ToString();
                    await HandleWebSocketConnection(clientId, webSocket, context);
                }
                else
                {
                    context.Request.Path = "/" + remainingPath;
                    await HandleHttpRequest(context);
                }
            });

            await _app.StartAsync();
            _isRunning = true;
            LogConnectionUrls();
        }

        private async Task HandleWebSocketConnection(string clientId, WebSocket webSocket, HttpContext context)
        {
            try
            {
                var clientIp = context.Connection.RemoteIpAddress;
                if (clientIp == null || !IsIpAllowed(clientIp))
                {
                    Logger.Log($"WebSocket connection rejected for IP: {clientIp}");
                    await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Access denied", CancellationToken.None);
                    return;
                }

                _connectedClients[clientId] = webSocket;
                _authenticatedClients.Add(clientId);

                // Send initial state for all enabled features
                foreach (var handler in _featureHandlers)
                {
                    await handler.BroadcastStateAsync();
                }

                var buffer = new byte[MaxMessageSize];
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client requested close", CancellationToken.None);
                        break;
                    }

                    // Re-check IP access on each message
                    if (!IsIpAllowed(clientIp))
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await HandleWebSocketMessage(message, clientId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"WebSocket error for client {clientId}: {ex.Message}");
            }
            finally
            {
                _connectedClients.TryRemove(clientId, out _);
                _authenticatedClients.Remove(clientId);
                _rateLimiter.ClearHistory(clientId);
            }
        }

        private bool IsIpAllowed(IPAddress clientIp)
        {
            // Always allow localhost
            if (IsLocalRequest(clientIp))
            {
                return true;
            }

            // For IPv6 addresses
            if (clientIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (clientIp.IsIPv6LinkLocal)
                {
                    return false;
                }
                return clientIp.IsIPv6SiteLocal || IsIPv6UniqueLocal(clientIp);
            }

            // For IPv4 addresses - check against enabled subnets
            foreach (var subnet in _enabledSubnets)
            {
                if (subnet.IsMatch(clientIp))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task SendWebSocketMessage(WebSocket webSocket, object message)
        {
            if (webSocket.State != WebSocketState.Open) return;

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task HandleWebSocketMessage(string message, string clientId)
        {
            try
            {
                var request = JsonSerializer.Deserialize<WebSocketRequest>(message);
                if (request == null) return;

                var messageType = request.Type.ToLower();
                var baseFeatureType = messageType.StartsWith("get") ? messageType[3..] : messageType;

                if ((baseFeatureType.Contains("speaker") && !IsSpeakerEnabled) ||
                    (baseFeatureType.Contains("microphone") && !IsMicrophoneEnabled) ||
                    (baseFeatureType.Contains("mouse") && !IsMouseEnabled) ||
                    (baseFeatureType.Contains("display") && !IsDisplayEnabled))
                {
                    await SendWebSocketMessage(_connectedClients[clientId],
                        new { type = "error", message = $"{baseFeatureType} disabled" });
                    return;
                }

                var handler = _featureHandlers.FirstOrDefault(h => h.FeatureType == baseFeatureType);
                if (handler != null)
                {
                    // If this is a state request and the feature is enabled, send the current state
                    if (messageType.StartsWith("get"))
                    {
                        await handler.BroadcastStateAsync();
                    }
                    else
                    {
                        await handler.HandleMessageAsync(JsonSerializer.SerializeToElement(request));
                    }
                }
            }
            catch (Exception ex)
            {
                await SendWebSocketMessage(_connectedClients[clientId],
                    new { type = "error", message = "Internal server error" });
                Logger.Log($"Error handling WebSocket message: {ex.Message}");
            }
        }

        private async Task HandleHttpRequest(HttpContext context)
        {
            var path = context.Request.Path.Value?.TrimStart('/') ?? "";
            var clientIp = context.Connection.RemoteIpAddress;

            Logger.Log($"Processing HTTP request for path: {path} from IP: {clientIp}");

            if (clientIp == null || !IsIpAllowed(clientIp))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied");
                return;
            }

            if (path.EndsWith(".css") || path.EndsWith(".js") ||
                path.EndsWith(".woff") || path.EndsWith(".ico") || path.EndsWith(".map"))
            {
                await ServeStaticFile(context, path);
                return;
            }
            await ServeMainHtml(context);
        }

        private async Task ServeMainHtml(HttpContext context)
        {
            using var stream = typeof(WebServer).Assembly.GetManifestResourceStream(HtmlResource);
            if (stream != null)
            {
                context.Response.ContentType = "text/html";
                await stream.CopyToAsync(context.Response.Body);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }

        private async Task ServeStaticFile(HttpContext context, string path)
        {
            try
            {
                context.Response.ContentType = path.ToLower() switch
                {
                    var ext when ext.EndsWith(".css") => "text/css; charset=utf-8",
                    var ext when ext.EndsWith(".js") => "application/javascript; charset=utf-8",
                    var ext when ext.EndsWith(".woff") => "font/woff",
                    var ext when ext.EndsWith(".ico") => "image/x-icon",
                    var ext when ext.EndsWith(".map") => "application/json; charset=utf-8",
                    _ => "application/octet-stream"
                };

                context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Append("Access-Control-Allow-Methods", "GET");

                var resourceNames = new[]
                {
                    $"WinControlCenter.{path.Replace('/', '.')}",  // Standard format
                    $"WinControlCenter.{path}",                    // Direct format
                    path                                           // Raw format
                };

                foreach (var resourceName in resourceNames)
                {
                    using var stream = typeof(WebServer).Assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        await stream.CopyToAsync(context.Response.Body);
                        return;
                    }
                }

                // If we get here, no resource was found
                Logger.Log($"Resource not found in any format: {path}");
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync($"Resource not found: {path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error serving static file {path}: {ex.Message}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"Error serving file: {ex.Message}");
            }
        }

        public async Task BroadcastMessageAsync(object message)
        {
            await _broadcastSemaphore.WaitAsync();
            try
            {
                var deadSockets = new List<string>();

                foreach (var client in _connectedClients)
                {
                    try
                    {
                        if (client.Value.State == WebSocketState.Open)
                        {
                            await SendWebSocketMessage(client.Value, message);
                        }
                        else
                        {
                            deadSockets.Add(client.Key);
                        }
                    }
                    catch
                    {
                        deadSockets.Add(client.Key);
                    }
                }

                foreach (var socketId in deadSockets)
                {
                    _connectedClients.TryRemove(socketId, out _);
                    _authenticatedClients.Remove(socketId);
                }
            }
            finally
            {
                _broadcastSemaphore.Release();
            }
        }

        public async Task StopWebServerAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;

            foreach (var client in _connectedClients.Values)
            {
                if (client.State == WebSocketState.Open)
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None);
                }
            }

            _connectedClients.Clear();
            _authenticatedClients.Clear();

            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        public void StopWebServer() => _ = StopWebServerAsync();
        public async Task StartAsync() => await StartWebServer();
        public void Stop() => StopWebServer();

        public void SetPassword(string newPassword)
        {
            if (!Regex.IsMatch(newPassword, "^(?=.*[a-z])(?=.*[A-Z])[a-zA-Z0-9]{8}$"))
            {
                throw new ArgumentException("Password must be exactly 8 characters long, contain at least one lowercase letter, one uppercase letter, and only letters and numbers.");
            }

            _accessPassword = newPassword;
            SaveSettings();
        }

        private bool IsLocalRequest(IPAddress clientIp)
        {
            return clientIp.Equals(IPAddress.Loopback) || 
                   clientIp.Equals(IPAddress.IPv6Loopback) ||
                   clientIp.Equals(IPAddress.Parse("::1"));
        }

        private bool IsIPv6UniqueLocal(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            // ULA addresses start with fc00::/7
            return (bytes[0] & 0xFE) == 0xFC;
        }

        public bool IsSubnetEnabled(string networkAddress, string subnetMask)
        {
            return _enabledSubnets.Contains(new NetworkSubnet(networkAddress, subnetMask));
        }

        public void AddAllowedSubnet(string networkAddress, string subnetMask)
        {
            var subnet = new NetworkSubnet(networkAddress, subnetMask);
            if (_enabledSubnets.Add(subnet))
            {
                SaveSettings();
                DisconnectUnauthorizedClients();
            }
        }

        public void RemoveAllowedSubnet(string networkAddress, string subnetMask)
        {
            var subnet = new NetworkSubnet(networkAddress, subnetMask);
            if (_enabledSubnets.Remove(subnet))
            {
                SaveSettings();
                DisconnectUnauthorizedClients();
            }
        }

        private async void DisconnectUnauthorizedClients()
        {
            var clientsToDisconnect = new List<(string Id, WebSocket Socket)>();

            foreach (var client in _connectedClients)
            {
                var context = _app?.Services.GetService<IHttpContextAccessor>()?.HttpContext;
                if (context?.Connection.RemoteIpAddress != null && !IsIpAllowed(context.Connection.RemoteIpAddress))
                {
                    clientsToDisconnect.Add((client.Key, client.Value));
                }
            }

            foreach (var (id, socket) in clientsToDisconnect)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked", CancellationToken.None);
                    }
                    _connectedClients.TryRemove(id, out _);
                    _authenticatedClients.Remove(id);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error disconnecting client {id}: {ex.Message}");
                }
            }
        }

        public void UpdateMouseSettings(double sensitivity, double scrollSensitivity, double accelerationFactor, double maxAcceleration)
        {
            if (_mouseApi is MouseKeyHookManager mouseManager)
            {
                mouseManager.UpdateSensitivitySettings(
                    sensitivity,
                    scrollSensitivity,
                    accelerationFactor,
                    maxAcceleration
                );
                SaveSettings();
            }
        }

        public MouseState GetMouseState()
        {
            if (_mouseApi is MouseKeyHookManager mouseManager)
            {
                return mouseManager.GetMouseState();
            }
            return new MouseState
            {
                IsEnabled = false,
                Sensitivity = MouseDefaults.Sensitivity,
                ScrollSensitivity = MouseDefaults.ScrollSensitivity,
                AccelerationFactor = MouseDefaults.AccelerationFactor,
                MaxAcceleration = MouseDefaults.MaxAcceleration
            };
        }

        public void RegeneratePassword()
        {
            // Avoiding confusing characters like O0, I1, l, etc.
            const string lowerChars = "abcdefghijkmnpqrstuvwxyz"; // removed l
            const string upperChars = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // removed I, O
            const string numbers = "23456789"; // removed 0, 1
            var random = new Random();

            // Ensure at least one lowercase and one uppercase letter
            var password = new char[8];
            password[0] = lowerChars[random.Next(lowerChars.Length)];
            password[1] = upperChars[random.Next(upperChars.Length)];

            // Fill up with random characters
            var allChars = lowerChars + upperChars + numbers;
            for (int i = 2; i < 8; i++)
            {
                password[i] = allChars[random.Next(allChars.Length)];
            }

            // Shuffle the password
            _accessPassword = new string(password.OrderBy(x => random.Next()).ToArray());
            SaveSettings();
        }

        private void UpdateStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null)
                {
                    Logger.Log("Failed to access registry key for startup configuration");
                    return;
                }

                if (enable)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                    {
                        Logger.Log("Failed to get application path for startup configuration");
                        return;
                    }
                    key.SetValue("WinControlCenter", $"\"{exePath}\"");  // Add quotes to handle paths with spaces
                    Logger.Log($"Added startup registry entry: {exePath}");
                }
                else
                {
                    key.DeleteValue("WinControlCenter", false);
                    Logger.Log("Removed startup registry entry");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating startup registry: {ex.Message}");
                throw; // Rethrow to notify UI
            }
        }
    }
}