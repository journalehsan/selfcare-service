using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.Collections;
#if WINDOWS
using AudioSwitcher.AudioApi.CoreAudio;
using System.Management;
#if NETFRAMEWORK || NET5_0_WINDOWS || NET6_0_WINDOWS || NET7_0_WINDOWS || NET8_0_WINDOWS || NET9_0_WINDOWS
using System.Windows.Forms;
using System.Drawing;
#endif
#endif

namespace SelfcareService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private TcpListener? _tcpListener;
    private int _port = 8080;
    private SecureAuthenticator? _secureAuth;
    private Timer? _uptimeMonitorTimer;
    private Timer? _tokenRefreshTimer;
    private UptimeMonitor _uptimeMonitor;
    private readonly HttpClient _httpClient;
    private readonly string _tokenFilePath;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _uptimeMonitor = new UptimeMonitor(_logger);
        
        // Initialize HTTP client for token fetching
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        
        // Set up token file path
        var appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var selfcareDir = Path.Combine(appDataLocal, "selfcare");
        Directory.CreateDirectory(selfcareDir); // Ensure directory exists
        _tokenFilePath = Path.Combine(selfcareDir, "auth_token.json");
        
        _logger.LogInformation($"Token file path: {_tokenFilePath}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Selfcare Service starting...");

        // Initialize secure authentication
        _secureAuth = new SecureAuthenticator();
        _secureAuth.Initialize();

        // Find available port and start TCP listener
        await StartTcpListener(stoppingToken);

        // Write port to file for Rust binaries to discover
        await WritePortFile();

        // Start uptime monitoring
        StartUptimeMonitoring();

        // Start token refresh monitoring (every 10 minutes)
        StartTokenRefreshMonitoring();

        // Fetch initial token immediately on startup (don't wait for first timer tick)
        _ = Task.Run(async () => {
            try
            {
                _logger.LogInformation("🚀 Fetching initial authentication token on service startup...");
                _logger.LogInformation($"📁 Token file location: {_tokenFilePath}");
                _logger.LogInformation("⏰ Token refresh interval: Every 10 minutes");
                await Task.Delay(3000); // Small delay to ensure service is fully initialized
                await RefreshTokenAsync();
                _logger.LogInformation("✅ Initial token fetch completed on startup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error fetching initial token on startup");
            }
        });

        // Start accepting connections
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_tcpListener != null)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client, stoppingToken), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting TCP client");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task StartTcpListener(CancellationToken cancellationToken)
    {
        while (_tcpListener == null && _port <= 8099)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, _port);
                _tcpListener.Start();
                _logger.LogInformation($"TCP listener started on port {_port}");
                break;
            }
            catch (SocketException)
            {
                _tcpListener?.Stop();
                _tcpListener = null;
                _port++;
                _logger.LogWarning($"Port {_port - 1} in use, trying {_port}");
            }
        }

        if (_tcpListener == null)
        {
            throw new Exception("No available port found in range 8080-8099");
        }
    }

    private async Task WritePortFile()
    {
        string portFilePath = GetPortFilePath();

        try
        {
            await File.WriteAllTextAsync(portFilePath, _port.ToString());

            // On Windows, also write to a common location accessible by all users
            if (OperatingSystem.IsWindows())
            {
                // Write to ProgramData which is accessible by all users
                string programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string selfcareDir = Path.Combine(programDataPath, "SelfCare");

                try
                {
                    // Create directory if it doesn't exist
                    Directory.CreateDirectory(selfcareDir);

                    string commonPortFile = Path.Combine(selfcareDir, "selfcare_port.txt");
                    await File.WriteAllTextAsync(commonPortFile, _port.ToString());

                    _logger.LogInformation($"Port file also written to common location: {commonPortFile}");
                }
                catch (Exception commonEx)
                {
                    _logger.LogWarning(commonEx, $"Failed to write port file to ProgramData location");
                }
            }
            else
            {
                // Linux: Also write to /tmp for compatibility with regular users
                string tmpPortFile = "/tmp/selfcare_port.txt";
                try
                {
                    await File.WriteAllTextAsync(tmpPortFile, _port.ToString());
                    _logger.LogInformation($"Port file also written to {tmpPortFile}");
                }
                catch (Exception tmpEx)
                {
                    _logger.LogWarning(tmpEx, $"Failed to write port file to {tmpPortFile}");
                }
            }

            // Set secure permissions on Unix systems
            if (!OperatingSystem.IsWindows())
            {
                var chmod = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 {portFilePath}",
                    UseShellExecute = false,
                    WorkingDirectory = "/tmp" // Safe working directory for chmod
                };
                using var process = Process.Start(chmod);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }

                // Make tmp file readable by all users
                try
                {
                    var tmpPortFile = "/tmp/selfcare_port.txt";
                    var chmodTmp = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"644 {tmpPortFile}",
                        UseShellExecute = false,
                        WorkingDirectory = "/tmp" // Safe working directory for chmod
                    };
                    using var tmpProcess = Process.Start(chmodTmp);
                    if (tmpProcess != null)
                    {
                        await tmpProcess.WaitForExitAsync();
                    }
                }
                catch (Exception chmodEx)
                {
                    _logger.LogWarning(chmodEx, $"Failed to set permissions on /tmp/selfcare_port.txt");
                }
            }

            _logger.LogInformation($"Port file written to {portFilePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to write port file to {portFilePath}");
            throw;
        }
    }

    private string GetPortFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Path.GetTempPath(), "selfcare_port.txt");
        }
        else
        {
            // Use user's home directory instead of /tmp for better permissions
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".selfcare_port.txt");
        }
    }

    private string GenerateAuthKey()
    {
        var hostname = Dns.GetHostName();
        var time = DateTime.UtcNow.ToString("HH00", CultureInfo.InvariantCulture);

        string data;
        if (OperatingSystem.IsWindows())
        {
            // Windows: Use only hostname + time to avoid username mismatch
            // between LocalSystem service and user app
            data = String.Concat(hostname, time);
            _logger.LogDebug($"Auth Debug - Platform: Windows, Hostname: {hostname}, Data: {data}");
        }
        else
        {
            // Linux/Unix: Use hostname + username + time for backward compatibility
            var username = Environment.UserName;
            data = String.Concat(hostname, username, time);
            _logger.LogDebug($"Auth Debug - Platform: Linux/Unix, Hostname: {hostname}, Username: {username}, Data: {data}");
        }

        _logger.LogDebug($"Auth Debug - Time UTC: {time}");

        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(data);
        var hash = sha256.ComputeHash(bytes);
        var authKey = Convert.ToBase64String(hash);

        _logger.LogDebug($"Auth Debug - Generated key length: {authKey.Length}");
        return authKey;
    }

    private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Client connected");

        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);

                if (bytesRead > 0)
                {
                    var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    _logger.LogInformation($"Received request from client");

                    // Check if this is a WMI query request with headers
                    if (request.Contains("HEADER:wmi_query=true"))
                    {
                        _logger.LogInformation("Processing WMI query request with headers");
                        var wmiResponse = await ProcessWmiQueryRequest(request);
                        var wmiResponseBytes = Encoding.UTF8.GetBytes(wmiResponse);

                        await stream.WriteAsync(wmiResponseBytes, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        return;
                    }

                    // Extract auth key and actual request (legacy format)
                    var lines = request.Split('\n', 2);
                    if (lines.Length < 2)
                    {
                        _logger.LogWarning("Request missing authentication key");
                        return;
                    }

                    var clientToken = lines[0].Trim();
                    var actualRequest = lines[1];

                    // Simple basic authentication for testing
                    string expectedToken = "selfcare:SelfCare@#2025";
                    if (clientToken != expectedToken)
                    {
                        _logger.LogWarning($"Basic authentication failed. Expected: '{expectedToken}', Received: '{clientToken}'");
                        return;
                    }

                    _logger.LogInformation("Authentication successful");
                    var response = await ProcessRequest(actualRequest);
                    var responseBytes = Encoding.UTF8.GetBytes(response);

                    await stream.WriteAsync(responseBytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client");
        }
    }

    private async Task<string> ProcessWmiQueryRequest(string request)
    {
        try
        {
            // Parse WMI request with headers
            var wmiRequest = WmiQueryRequest.Parse(request);

            // Validate authentication if present
            if (wmiRequest.Headers.ContainsKey("auth"))
            {
                var authToken = wmiRequest.Headers["auth"];
                if (authToken != "selfcare:SelfCare@#2025")
                {
                    _logger.LogWarning($"WMI authentication failed");
                    return JsonSerializer.Serialize(new WmiQueryResponse
                    {
                        Success = false,
                        Error = "Authentication failed",
                        Data = null
                    });
                }
            }

            // Create WMI handler with typed logger
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var wmiLogger = loggerFactory.CreateLogger<WmiQueryHandler>();
            var wmiHandler = new WmiQueryHandler(wmiLogger);

            // Validate query safety
            if (!wmiHandler.IsQuerySafe(wmiRequest.Query))
            {
                return JsonSerializer.Serialize(new WmiQueryResponse
                {
                    Success = false,
                    Error = "Query contains unsafe operations",
                    Data = null
                });
            }

            // Process the WMI query
            var response = await wmiHandler.ProcessWmiQuery(wmiRequest.Query, wmiRequest.Headers);

            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WMI request");
            return JsonSerializer.Serialize(new WmiQueryResponse
            {
                Success = false,
                Error = ex.Message,
                Data = null
            });
        }
    }

    private async Task<string> ProcessRequest(string request)
    {
        try
        {
            var serviceRequest = JsonSerializer.Deserialize<ServiceRequest>(request);

            return serviceRequest?.Type switch
            {
                "RunCommand" => await HandleRunCommand(serviceRequest.Command, serviceRequest.Arguments),
                "ExecuteScript" => await HandleExecuteScript(serviceRequest.Command, serviceRequest.Data),
                "GetSystemStatus" => await HandleSystemStatus(),
                "CheckPrivileges" => await HandleCheckPrivileges(),
                "AudioMethod" => await HandleAudioControl(serviceRequest.Command, serviceRequest.Arguments),
                "DateTimeControl" => await HandleDateTimeControl(serviceRequest.Command, serviceRequest.Arguments),
                "UptimeCheck" => await HandleUptimeCheck(),
                "ShowRebootWarning" => await HandleShowRebootWarning(),
                "GetUptimeStatus" => await HandleGetUptimeStatus(),
                "RefreshToken" => await HandleRefreshToken(),
                "GetTokenStatus" => await HandleGetTokenStatus(),
                _ => JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "Unknown request type",
                    Output = ""
                })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    /// <summary>
    /// Process command arguments to handle paths with spaces and special characters
    /// </summary>
    private string ProcessCommandArguments(string? arguments, string commandPath)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return "";
        }

        _logger.LogDebug($"Processing arguments: '{arguments}' for command: '{commandPath}'");

        try
        {
            // Strategy 1: If arguments already contain quoted paths, leave them as is
            if (arguments.Contains("\"") || arguments.Contains("'"))
            {
                _logger.LogDebug("Arguments already contain quotes, using as-is");
                return arguments;
            }

            // Strategy 2: Check if this is a script or batch file execution
            var commandLower = commandPath.ToLower();
            var isScriptExecution = commandLower.EndsWith(".bat") || commandLower.EndsWith(".cmd") || 
                                  commandLower.EndsWith(".ps1") || commandLower.EndsWith(".vbs") ||
                                  commandLower.Contains("cmd.exe") || commandLower.Contains("powershell");

            if (isScriptExecution)
            {
                return ProcessScriptArguments(arguments);
            }

            // Strategy 3: Check if arguments contain paths that need quoting
            var processedArgs = ProcessPathArguments(arguments);
            
            _logger.LogDebug($"Processed arguments: '{processedArgs}'");
            return processedArgs;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to process arguments '{arguments}': {ex.Message}, using original");
            return arguments;
        }
    }

    /// <summary>
    /// Process arguments for script execution (batch files, PowerShell, etc.)
    /// </summary>
    private string ProcessScriptArguments(string arguments)
    {
        _logger.LogDebug($"Processing script arguments: '{arguments}'");

        // For scripts, we need to be more careful about quoting
        var parts = SplitArgumentsPreservingQuotes(arguments);
        var processedParts = new List<string>();

        foreach (var part in parts)
        {
            if (NeedsQuoting(part))
            {
                processedParts.Add($"\"{EscapeForQuotes(part)}\"");
                _logger.LogDebug($"Quoted argument: '{part}' -> '\"{EscapeForQuotes(part)}\"'");
            }
            else
            {
                processedParts.Add(part);
            }
        }

        return string.Join(" ", processedParts);
    }

    /// <summary>
    /// Process arguments for regular command execution
    /// </summary>
    private string ProcessPathArguments(string arguments)
    {
        _logger.LogDebug($"Processing path arguments: '{arguments}'");

        var parts = SplitArgumentsPreservingQuotes(arguments);
        var processedParts = new List<string>();

        foreach (var part in parts)
        {
            // Check if this part looks like a file path
            if (IsLikelyFilePath(part))
            {
                if (NeedsQuoting(part))
                {
                    processedParts.Add($"\"{EscapeForQuotes(part)}\"");
                    _logger.LogDebug($"Quoted file path: '{part}' -> '\"{EscapeForQuotes(part)}\"'");
                }
                else
                {
                    processedParts.Add(part);
                }
            }
            else
            {
                processedParts.Add(part);
            }
        }

        return string.Join(" ", processedParts);
    }

    /// <summary>
    /// Split arguments while preserving existing quotes
    /// </summary>
    private List<string> SplitArgumentsPreservingQuotes(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];

            if (!inQuotes && (c == '"' || c == '\''))
            {
                inQuotes = true;
                quoteChar = c;
                current.Append(c);
            }
            else if (inQuotes && c == quoteChar)
            {
                inQuotes = false;
                current.Append(c);
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>
    /// Check if a string needs to be quoted
    /// </summary>
    private bool NeedsQuoting(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        // Check for spaces, special characters, or paths
        return input.Contains(' ') || 
               input.Contains('&') || 
               input.Contains('|') || 
               input.Contains('<') || 
               input.Contains('>') ||
               input.Contains('^') ||
               input.Contains('%') ||
               input.Contains('!') ||
               input.Contains('(') ||
               input.Contains(')') ||
               input.Contains('[') ||
               input.Contains(']') ||
               input.Contains('{') ||
               input.Contains('}') ||
               input.Contains(';') ||
               input.Contains(',') ||
               (input.Contains(":\\") && input.Length > 3); // Likely a Windows path
    }

    /// <summary>
    /// Check if a string looks like a file path
    /// </summary>
    private bool IsLikelyFilePath(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        // Windows path patterns
        if (input.Length >= 3 && input[1] == ':' && input[2] == '\\')
            return true;

        // UNC path patterns
        if (input.StartsWith("\\\\"))
            return true;

        // Relative path patterns
        if (input.Contains("\\") || input.Contains("/"))
            return true;

        // File extension patterns
        var extensions = new[] { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".zip", ".txt", ".log", ".xml", ".json", ".config" };
        return extensions.Any(ext => input.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Escape special characters for quoting
    /// </summary>
    private string EscapeForQuotes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Escape backslashes and quotes
        return input.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"");
    }

    /// <summary>
    /// Comprehensive Windows command resolution with multiple fallback strategies
    /// </summary>
    private string ResolveWindowsCommand(string command)
    {
        var commandLower = command.ToLower();
        _logger.LogDebug($"Resolving Windows command: {command}");

        // Strategy 1: Direct mapping for known commands
        var directMappings = new Dictionary<string, string[]>
        {
            ["tasklist"] = new[] { @"C:\Windows\System32\tasklist.exe" },
            ["taskkill"] = new[] { @"C:\Windows\System32\taskkill.exe" },
            ["wmic"] = new[] { @"C:\Windows\System32\wbem\wmic.exe" },
            ["whoami"] = new[] { @"C:\Windows\System32\whoami.exe" },
            ["ipconfig"] = new[] { @"C:\Windows\System32\ipconfig.exe" },
            ["ping"] = new[] { @"C:\Windows\System32\ping.exe" },
            ["netstat"] = new[] { @"C:\Windows\System32\netstat.exe" },
            ["systeminfo"] = new[] { @"C:\Windows\System32\systeminfo.exe" },
            ["schtasks"] = new[] { @"C:\Windows\System32\schtasks.exe" },
            ["reg"] = new[] { @"C:\Windows\System32\reg.exe" },
            ["sc"] = new[] { @"C:\Windows\System32\sc.exe" },
            ["powershell"] = new[] { 
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
            },
            ["cmd"] = new[] { @"C:\Windows\System32\cmd.exe" },
            ["tzutil"] = new[] { @"C:\Windows\System32\tzutil.exe" },
            ["w32tm"] = new[] { @"C:\Windows\System32\w32tm.exe" },
            ["gpupdate"] = new[] { @"C:\Windows\System32\gpupdate.exe" },
            ["gpresult"] = new[] { @"C:\Windows\System32\gpresult.exe" },
            ["net"] = new[] { @"C:\Windows\System32\net.exe" },
            ["netsh"] = new[] { @"C:\Windows\System32\netsh.exe" },
            ["nslookup"] = new[] { @"C:\Windows\System32\nslookup.exe" },
            ["tracert"] = new[] { @"C:\Windows\System32\tracert.exe" },
            ["telnet"] = new[] { @"C:\Windows\System32\telnet.exe" },
            ["ftp"] = new[] { @"C:\Windows\System32\ftp.exe" },
            ["robocopy"] = new[] { @"C:\Windows\System32\robocopy.exe" },
            ["xcopy"] = new[] { @"C:\Windows\System32\xcopy.exe" },
            ["copy"] = new[] { @"C:\Windows\System32\copy.exe" },
            ["move"] = new[] { @"C:\Windows\System32\move.exe" },
            ["del"] = new[] { @"C:\Windows\System32\del.exe" },
            ["dir"] = new[] { @"C:\Windows\System32\dir.exe" },
            ["type"] = new[] { @"C:\Windows\System32\type.exe" },
            ["findstr"] = new[] { @"C:\Windows\System32\findstr.exe" },
            ["find"] = new[] { @"C:\Windows\System32\find.exe" },
            ["sort"] = new[] { @"C:\Windows\System32\sort.exe" },
            ["more"] = new[] { @"C:\Windows\System32\more.com" },
            ["tree"] = new[] { @"C:\Windows\System32\tree.com" },
            ["attrib"] = new[] { @"C:\Windows\System32\attrib.exe" },
            ["fc"] = new[] { @"C:\Windows\System32\fc.exe" },
            ["comp"] = new[] { @"C:\Windows\System32\comp.exe" },
            ["diskpart"] = new[] { @"C:\Windows\System32\diskpart.exe" },
            ["format"] = new[] { @"C:\Windows\System32\format.com" },
            ["chkdsk"] = new[] { @"C:\Windows\System32\chkdsk.exe" },
            ["sfc"] = new[] { @"C:\Windows\System32\sfc.exe" },
            ["dism"] = new[] { @"C:\Windows\System32\dism.exe" },
            ["bcdedit"] = new[] { @"C:\Windows\System32\bcdedit.exe" },
            ["bootrec"] = new[] { @"C:\Windows\System32\bootrec.exe" },
            ["msinfo32"] = new[] { @"C:\Program Files\Common Files\Microsoft Shared\MSInfo\msinfo32.exe" },
            ["dxdiag"] = new[] { @"C:\Windows\System32\dxdiag.exe" },
            ["mstsc"] = new[] { @"C:\Windows\System32\mstsc.exe" },
            ["winver"] = new[] { @"C:\Windows\System32\winver.exe" },
            ["msconfig"] = new[] { @"C:\Windows\System32\msconfig.exe" },
            ["services.msc"] = new[] { @"C:\Windows\System32\services.msc" },
            ["eventvwr"] = new[] { @"C:\Windows\System32\eventvwr.exe" },
            ["perfmon"] = new[] { @"C:\Windows\System32\perfmon.exe" },
            ["taskmgr"] = new[] { @"C:\Windows\System32\taskmgr.exe" },
            ["control"] = new[] { @"C:\Windows\System32\control.exe" },
            ["appwiz.cpl"] = new[] { @"C:\Windows\System32\appwiz.cpl" },
            ["sysdm.cpl"] = new[] { @"C:\Windows\System32\sysdm.cpl" },
            ["ncpa.cpl"] = new[] { @"C:\Windows\System32\ncpa.cpl" },
            ["firewall.cpl"] = new[] { @"C:\Windows\System32\firewall.cpl" }
        };

        // Strategy 2: Check direct mappings first
        if (directMappings.ContainsKey(commandLower))
        {
            foreach (var path in directMappings[commandLower])
            {
                if (File.Exists(path))
                {
                    _logger.LogDebug($"Found command via direct mapping: {path}");
                    return path;
                }
            }
            _logger.LogWarning($"Direct mapping found but none of the paths exist for command: {command}");
        }

        // Strategy 3: Check if command already has a full path
        if (Path.IsPathRooted(command))
        {
            if (File.Exists(command))
            {
                _logger.LogDebug($"Command already has full path and exists: {command}");
                return command;
            }
            _logger.LogWarning($"Full path command does not exist: {command}");
        }

        // Strategy 4: Try common Windows directories
        var searchPaths = new[]
        {
            @"C:\Windows\System32",
            @"C:\Windows\System32\wbem",
            @"C:\Windows\System32\WindowsPowerShell\v1.0",
            @"C:\Program Files\PowerShell\7",
            @"C:\Windows\SysWOW64", // For 32-bit processes on 64-bit systems
            @"C:\Windows\SysWOW64\wbem",
            @"C:\Windows",
            @"C:\Program Files\Common Files\Microsoft Shared",
            @"C:\Program Files (x86)\Common Files\Microsoft Shared",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var extensions = new[] { ".exe", ".com", ".cmd", ".bat", ".msc", ".cpl" };

        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;

            foreach (var extension in extensions)
            {
                var fullPath = Path.Combine(searchPath, command + extension);
                if (File.Exists(fullPath))
                {
                    _logger.LogDebug($"Found command in search path: {fullPath}");
                    return fullPath;
                }
            }
        }

        // Strategy 5: Use where.exe to find the command (if available)
        try
        {
            var wherePath = @"C:\Windows\System32\where.exe";
            if (File.Exists(wherePath))
            {
                var whereProcess = new ProcessStartInfo
                {
                    FileName = wherePath,
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(whereProcess);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000); // 5 second timeout

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var firstPath = output.Split('\n')[0].Trim();
                        if (File.Exists(firstPath))
                        {
                            _logger.LogDebug($"Found command via where.exe: {firstPath}");
                            return firstPath;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to use where.exe for command '{command}': {ex.Message}");
        }

        // Strategy 6: Try with different extensions in current directory
        foreach (var extension in extensions)
        {
            var currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), command + extension);
            if (File.Exists(currentDirPath))
            {
                _logger.LogDebug($"Found command in current directory: {currentDirPath}");
                return currentDirPath;
            }
        }

        // Strategy 7: Last resort - return original command and let the system try to resolve it
        _logger.LogWarning($"Could not resolve full path for command: {command}, using original");
        return command;
    }

    /// <summary>
    /// Setup comprehensive Windows environment for process execution
    /// </summary>
    private void SetupWindowsEnvironment(ProcessStartInfo processInfo)
    {
        try
        {
            // Get current environment variables
            var currentEnv = Environment.GetEnvironmentVariables();
            
            // Copy all existing environment variables
            foreach (DictionaryEntry entry in currentEnv)
            {
                var key = entry.Key?.ToString();
                var value = entry.Value?.ToString();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    processInfo.EnvironmentVariables[key] = value;
                }
            }

            // Enhanced PATH setup
            var systemPaths = new[]
            {
                @"C:\Windows\System32",
                @"C:\Windows\System32\wbem",
                @"C:\Windows\System32\WindowsPowerShell\v1.0",
                @"C:\Program Files\PowerShell\7",
                @"C:\Windows\SysWOW64",
                @"C:\Windows\SysWOW64\wbem",
                @"C:\Windows",
                @"C:\Program Files\Common Files\Microsoft Shared",
                @"C:\Program Files (x86)\Common Files\Microsoft Shared",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules"
            };

            var currentPath = processInfo.EnvironmentVariables["PATH"] ?? "";
            var enhancedPath = string.Join(";", systemPaths.Where(Directory.Exists).Distinct());
            
            if (!string.IsNullOrEmpty(currentPath))
            {
                processInfo.EnvironmentVariables["PATH"] = $"{currentPath};{enhancedPath}";
            }
            else
            {
                processInfo.EnvironmentVariables["PATH"] = enhancedPath;
            }

            // Set important Windows environment variables
            processInfo.EnvironmentVariables["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            processInfo.EnvironmentVariables["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            processInfo.EnvironmentVariables["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            processInfo.EnvironmentVariables["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            processInfo.EnvironmentVariables["CommonProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            
            // Set COMSPEC if not already set
            if (string.IsNullOrEmpty(processInfo.EnvironmentVariables["COMSPEC"]))
            {
                processInfo.EnvironmentVariables["COMSPEC"] = @"C:\Windows\System32\cmd.exe";
            }

            // Set PATHEXT for command resolution
            var pathExt = new[]
            {
                ".COM", ".EXE", ".BAT", ".CMD", ".VBS", ".VBE", ".JS", ".JSE", 
                ".WSF", ".WSH", ".MSC", ".CPL", ".SCR", ".PIF"
            };
            processInfo.EnvironmentVariables["PATHEXT"] = string.Join(";", pathExt);

            // Set Windows version information
            var osVersion = Environment.OSVersion;
            processInfo.EnvironmentVariables["OS"] = "Windows_NT";
            processInfo.EnvironmentVariables["PROCESSOR_ARCHITECTURE"] = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "x64";
            
            // Set locale information
            processInfo.EnvironmentVariables["LANG"] = System.Globalization.CultureInfo.CurrentCulture.Name;
            
            var pathValue = processInfo.EnvironmentVariables["PATH"];
            _logger.LogDebug($"Enhanced Windows environment setup completed. PATH contains {pathValue?.Split(';').Length ?? 0} directories");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup enhanced Windows environment, using defaults");
            
            // Fallback to basic PATH setup
            var basicPaths = @";C:\Windows\System32;C:\Windows\System32\wbem;C:\Windows\System32\WindowsPowerShell\v1.0";
            var currentPath = processInfo.EnvironmentVariables["PATH"] ?? "";
            processInfo.EnvironmentVariables["PATH"] = currentPath + basicPaths;
        }
    }

    private async Task<string> HandleRunCommand(string? command, string? arguments)
    {
        if (string.IsNullOrEmpty(command))
        {
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = "Command cannot be empty",
                Output = ""
            });
        }

        try
        {
            // Set a safe working directory that exists on both Windows and Linux
            string workingDirectory;
            if (OperatingSystem.IsWindows())
            {
                // On Windows, use the system temp directory
                workingDirectory = Path.GetTempPath();
            }
            else
            {
                // On Linux, use the user's home directory or /tmp as fallback
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (Directory.Exists(homeDir))
                {
                    workingDirectory = homeDir;
                }
                else if (Directory.Exists("/tmp"))
                {
                    workingDirectory = "/tmp";
                }
                else
                {
                    workingDirectory = "/"; // Last resort
                }
            }

            // Enhanced command resolution for Windows
            string fullCommandPath = command;
            if (OperatingSystem.IsWindows())
            {
                fullCommandPath = ResolveWindowsCommand(command);
            }

            // Properly handle arguments with spaces and special characters
            string processedArguments = ProcessCommandArguments(arguments, fullCommandPath);

            var processInfo = new ProcessStartInfo
            {
                FileName = fullCommandPath,
                Arguments = processedArguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            // Enhanced environment setup for Windows
            if (OperatingSystem.IsWindows())
            {
                SetupWindowsEnvironment(processInfo);
            }

            _logger.LogDebug($"Executing command '{fullCommandPath}' with working directory '{workingDirectory}'");

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            _logger.LogInformation($"Command '{fullCommandPath} {arguments}' executed with exit code {process.ExitCode}");

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "Command executed successfully" : $"Command failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error executing command '{command}'");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<string> HandleExecuteScript(string? scriptType, string? scriptContent)
    {
        if (string.IsNullOrEmpty(scriptType) || string.IsNullOrEmpty(scriptContent))
        {
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = "Script type and content cannot be empty",
                Output = ""
            });
        }

        try
        {
            // Create a safe temporary directory for script execution with fallback
            string tempDir;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), "selfcare_scripts");
                Directory.CreateDirectory(tempDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create preferred temp directory, using system temp");
                // Fallback to direct temp path
                tempDir = Path.GetTempPath();
                if (!Directory.Exists(tempDir))
                {
                    throw new InvalidOperationException("Cannot access any temporary directory for script execution");
                }
            }

            // Generate a unique filename based on script type
            string fileExtension = scriptType.ToLower() switch
            {
                "vbscript" => ".vbs",
                "batch" => ".bat",
                "powershell" => ".ps1",
                "python" => ".py",
                "perl" => ".pl",
                "bash" => ".sh",
                "sh" => ".sh",
                _ => ".tmp"
            };

            string scriptFileName = $"selfcare_script_{Guid.NewGuid():N}{fileExtension}";
            string scriptPath = Path.Combine(tempDir, scriptFileName);

            // Write script content to file
            await File.WriteAllTextAsync(scriptPath, scriptContent, Encoding.UTF8);

            _logger.LogInformation($"Executing {scriptType} script: {scriptPath}");

            // Execute the script based on type
            var result = scriptType.ToLower() switch
            {
                "vbscript" => await ExecuteVBScript(scriptPath),
                "batch" => await ExecuteBatchScript(scriptPath),
                "powershell" => await ExecutePowerShellScript(scriptPath),
                "python" => await ExecutePythonScript(scriptPath),
                "perl" => await ExecutePerlScript(scriptPath),
                "bash" or "sh" => await ExecuteShellScript(scriptPath, scriptType),
                _ => new ServiceResponse
                {
                    Success = false,
                    Message = $"Unsupported script type: {scriptType}",
                    Output = ""
                }
            };

            // Clean up the temporary script file
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete temporary script file: {scriptPath}");
            }

            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error executing {scriptType} script");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<ServiceResponse> ExecuteVBScript(string scriptPath)
    {
        try
        {
            // Resolve VBScript executor path using enhanced Windows command resolution
            string cscriptPath = ResolveWindowsCommand("cscript");
            
            // If cscript is not found, try alternative VBScript executors
            if (cscriptPath == "cscript" && !File.Exists(cscriptPath))
            {
                var vbsExecutors = new[]
                {
                    @"C:\Windows\System32\cscript.exe",
                    @"C:\Windows\SysWOW64\cscript.exe",
                    @"C:\Windows\System32\wscript.exe"
                };
                
                foreach (var executor in vbsExecutors)
                {
                    if (File.Exists(executor))
                    {
                        cscriptPath = executor;
                        break;
                    }
                }
                
                // If still not found, return informative error
                if (cscriptPath == "cscript")
                {
                    return new ServiceResponse
                    {
                        Success = false,
                        Message = "VBScript engine (cscript.exe) not found. VBScript may not be installed on this Windows system.",
                        Output = "Error: The system cannot find the path specified (VBScript engine missing)"
                    };
                }
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = cscriptPath,
                Arguments = cscriptPath.EndsWith("wscript.exe") ? 
                    $"\"{scriptPath}\"" : $"//NoLogo //B \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = GetSafeWorkingDirectory(scriptPath)
            };

            // Set environment variables to suppress VBScript dialogs
            processInfo.EnvironmentVariables["WSH_BATCH"] = "1";

            // Enhanced environment setup for Windows
            if (OperatingSystem.IsWindows())
            {
                SetupWindowsEnvironment(processInfo);
            }

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "VBScript executed successfully" : $"VBScript failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"VBScript execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<ServiceResponse> ExecuteBatchScript(string scriptPath)
    {
        try
        {
            // Resolve cmd.exe path using enhanced Windows command resolution
            string cmdPath = ResolveWindowsCommand("cmd");
            
            var processInfo = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            // Enhanced environment setup for Windows
            if (OperatingSystem.IsWindows())
            {
                SetupWindowsEnvironment(processInfo);
            }

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "Batch script executed successfully" : $"Batch script failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"Batch script execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<ServiceResponse> ExecutePowerShellScript(string scriptPath)
    {
        try
        {
            // Resolve PowerShell path using enhanced Windows command resolution
            string powershellPath = ResolveWindowsCommand("powershell");
            
            var processInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            // Enhanced environment setup for Windows
            if (OperatingSystem.IsWindows())
            {
                SetupWindowsEnvironment(processInfo);
            }

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "PowerShell script executed successfully" : $"PowerShell script failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"PowerShell script execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<ServiceResponse> ExecutePythonScript(string scriptPath)
    {
        try
        {
            // Try to find Python executable
            string pythonExe = "python";
            if (OperatingSystem.IsWindows())
            {
                // Try common Python installations on Windows
                var pythonPaths = new[]
                {
                    @"C:\Python39\python.exe",
                    @"C:\Python310\python.exe",
                    @"C:\Python311\python.exe",
                    @"C:\Python312\python.exe",
                    @"C:\Program Files\Python39\python.exe",
                    @"C:\Program Files\Python310\python.exe",
                    @"C:\Program Files\Python311\python.exe",
                    @"C:\Program Files\Python312\python.exe"
                };

                foreach (var path in pythonPaths)
                {
                    if (File.Exists(path))
                    {
                        pythonExe = path;
                        break;
                    }
                }
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "Python script executed successfully" : $"Python script failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"Python script execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<ServiceResponse> ExecutePerlScript(string scriptPath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "perl",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "Perl script executed successfully" : $"Perl script failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"Perl script execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<ServiceResponse> ExecuteShellScript(string scriptPath, string shellType)
    {
        try
        {
            // Make the script executable on Unix-like systems
            if (!OperatingSystem.IsWindows())
            {
                var chmodProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{scriptPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                chmodProcess.Start();
                await chmodProcess.WaitForExitAsync();
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = shellType.ToLower() switch
                {
                    "bash" => OperatingSystem.IsWindows() ? "bash" : "/bin/bash",
                    "sh" => OperatingSystem.IsWindows() ? "sh" : "/bin/sh",
                    _ => OperatingSystem.IsWindows() ? "bash" : "/bin/bash"
                },
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResponse
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? $"{shellType} script executed successfully" : $"{shellType} script failed with exit code {process.ExitCode}",
                Output = combinedOutput.Trim(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new ServiceResponse
            {
                Success = false,
                Message = $"{shellType} script execution error: {ex.Message}",
                Output = ""
            };
        }
    }

    private async Task<string> HandleSystemStatus()
    {
        var status = new
        {
            ServiceRunning = true,
            Platform = Environment.OSVersion.ToString(),
            ProcessId = Environment.ProcessId,
            WorkingDirectory = Environment.CurrentDirectory,
            IsElevated = IsRunningElevated()
        };

        return JsonSerializer.Serialize(new ServiceResponse
        {
            Success = true,
            Message = "System status retrieved",
            Output = JsonSerializer.Serialize(status)
        });
    }

    private async Task<string> HandleCheckPrivileges()
    {
        bool isElevated = IsRunningElevated();

        return JsonSerializer.Serialize(new ServiceResponse
        {
            Success = true,
            Message = isElevated ? "Running with elevated privileges" : "Running with normal privileges",
            Output = isElevated.ToString().ToLower()
        });
    }

    private async Task<string> HandleAudioControl(string? method, string? arguments)
    {
#if WINDOWS
        try
        {
            // Only available on Windows
            if (!OperatingSystem.IsWindows())
            {
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "Audio control is only available on Windows",
                    Output = ""
                });
            }

            var controller = new CoreAudioController();
            var playbackDevice = controller.DefaultPlaybackDevice;
            var captureDevice = controller.DefaultCaptureDevice;

            switch (method)
            {
                case "GetOutputVolume":
                    var outputVolume = Math.Min(100.0, Math.Max(0.0, playbackDevice.Volume));
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output volume retrieved",
                        Output = outputVolume.ToString("F0")
                    });

                case "GetInputVolume":
                    var inputVolume = Math.Min(100.0, Math.Max(0.0, captureDevice.Volume));
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input volume retrieved",
                        Output = inputVolume.ToString("F0")
                    });

                case "IsOutputMute":
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output mute status retrieved",
                        Output = playbackDevice.IsMuted.ToString().ToLower()
                    });

                case "IsInputMute":
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input mute status retrieved",
                        Output = captureDevice.IsMuted.ToString().ToLower()
                    });

                case "SetUnmuteAll":
                    await playbackDevice.SetMuteAsync(false);
                    await captureDevice.SetMuteAsync(false);
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "All devices unmuted",
                        Output = "unmuted"
                    });

                case "SetOutputVolume":
                    if (double.TryParse(arguments, out double newOutputVolume) && newOutputVolume >= 0 && newOutputVolume <= 100)
                    {
                        await playbackDevice.SetVolumeAsync(newOutputVolume);
                        return JsonSerializer.Serialize(new ServiceResponse
                        {
                            Success = true,
                            Message = $"Output volume set to {newOutputVolume}%",
                            Output = newOutputVolume.ToString()
                        });
                    }
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = false,
                        Message = "Invalid volume level. Must be between 0 and 100.",
                        Output = ""
                    });

                case "SetInputVolume":
                    if (double.TryParse(arguments, out double newInputVolume) && newInputVolume >= 0 && newInputVolume <= 100)
                    {
                        await captureDevice.SetVolumeAsync(newInputVolume);
                        return JsonSerializer.Serialize(new ServiceResponse
                        {
                            Success = true,
                            Message = $"Input volume set to {newInputVolume}%",
                            Output = newInputVolume.ToString()
                        });
                    }
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = false,
                        Message = "Invalid volume level. Must be between 0 and 100.",
                        Output = ""
                    });

                case "MuteOutput":
                    await playbackDevice.SetMuteAsync(true);
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output muted",
                        Output = "muted"
                    });

                case "MuteInput":
                    await captureDevice.SetMuteAsync(true);
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input muted",
                        Output = "muted"
                    });

                case "UnmuteOutput":
                    await playbackDevice.SetMuteAsync(false);
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output unmuted",
                        Output = "unmuted"
                    });

                case "UnmuteInput":
                    await captureDevice.SetMuteAsync(false);
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input unmuted",
                        Output = "unmuted"
                    });

                case "GetOutputDevices":
                    var outputDevices = new List<object>();
                    foreach (var device in controller.GetPlaybackDevices())
                    {
                        outputDevices.Add(new
                        {
                            Name = device.FullName,
                            Id = device.Id,
                            State = device.State.ToString(),
                            IsDefault = device.IsDefaultDevice
                        });
                    }
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output devices retrieved",
                        Output = JsonSerializer.Serialize(outputDevices)
                    });

                case "GetInputDevices":
                    var inputDevices = new List<object>();
                    foreach (var device in controller.GetCaptureDevices())
                    {
                        inputDevices.Add(new
                        {
                            Name = device.FullName,
                            Id = device.Id,
                            State = device.State.ToString(),
                            IsDefault = device.IsDefaultDevice
                        });
                    }
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input devices retrieved",
                        Output = JsonSerializer.Serialize(inputDevices)
                    });

                case "GetDefaultOutputDevice":
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Default output device retrieved",
                        Output = JsonSerializer.Serialize(new
                        {
                            Name = playbackDevice.FullName,
                            Id = playbackDevice.Id,
                            State = playbackDevice.State.ToString(),
                            Volume = Math.Min(100.0, Math.Max(0.0, playbackDevice.Volume)),
                            IsMuted = playbackDevice.IsMuted
                        })
                    });

                case "GetDefaultInputDevice":
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Default input device retrieved",
                        Output = JsonSerializer.Serialize(new
                        {
                            Name = captureDevice.FullName,
                            Id = captureDevice.Id,
                            State = captureDevice.State.ToString(),
                            Volume = Math.Min(100.0, Math.Max(0.0, captureDevice.Volume)),
                            IsMuted = captureDevice.IsMuted
                        })
                    });

                default:
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = false,
                        Message = $"Unknown audio method: {method}",
                        Output = ""
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in HandleAudioControl for method {method}");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = $"Audio control error: {ex.Message}",
                Output = ""
            });
        }
#else
        return JsonSerializer.Serialize(new ServiceResponse
        {
            Success = false,
            Message = "Audio control is only available on Windows",
            Output = ""
        });
#endif
    }

    private async Task<string> HandleDateTimeControl(string? method, string? arguments)
    {
        try
        {
            // Only available on Windows
            if (!OperatingSystem.IsWindows())
            {
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "DateTime control is only available on Windows",
                    Output = ""
                });
            }

            switch (method)
            {
                case "SetTimeZone":
                    return await SetIranTimeZone();

                case "FixDateTime":
                    return await FixDateTime();

                default:
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = false,
                        Message = $"Unknown datetime method: {method}",
                        Output = ""
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in HandleDateTimeControl for method {method}");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = $"DateTime control error: {ex.Message}",
                Output = ""
            });
        }
    }

    private async Task<string> SetIranTimeZone()
    {
        try
        {
            var logs = new List<string>();
            bool success = false;

            // Add overall timeout protection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)); // 25 second overall timeout
            var cancellationToken = cts.Token;

            // Method 1: Use .NET TimeZoneInfo (preferred)
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                logs.Add("Attempting to set timezone using .NET TimeZoneInfo...");

                // Find Iran Standard Time zone
                var iranTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");

                // Set the system timezone using Windows API calls through .NET
                var result = await SetSystemTimeZoneUsingDotNetWithTimeout(iranTimeZone, TimeSpan.FromSeconds(10), cancellationToken);

                if (result.success)
                {
                    logs.Add("✓ Successfully set Iran Standard Time using .NET");
                    success = true;
                }
                else
                {
                    logs.Add($"⚠ .NET method failed: {result.message}");
                }
            }
            catch (OperationCanceledException)
            {
                logs.Add("⚠ .NET timezone setting cancelled due to timeout");
            }
            catch (TimeZoneNotFoundException)
            {
                logs.Add("⚠ Iran Standard Time not found, trying alternative timezone...");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Fallback to West Asia Standard Time (UTC+3:30)
                    var westAsiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("West Asia Standard Time");
                    var result = await SetSystemTimeZoneUsingDotNetWithTimeout(westAsiaTimeZone, TimeSpan.FromSeconds(10), cancellationToken);

                    if (result.success)
                    {
                        logs.Add("✓ Successfully set West Asia Standard Time as fallback");
                        success = true;
                    }
                    else
                    {
                        logs.Add($"⚠ Fallback timezone method failed: {result.message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    logs.Add("⚠ Fallback timezone setting cancelled due to timeout");
                }
                catch (Exception ex)
                {
                    logs.Add($"⚠ Fallback timezone failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                logs.Add($"⚠ .NET timezone setting failed: {ex.Message}");
            }

            // Method 2: Fallback to tzutil command with timeout
            if (!success)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logs.Add("Falling back to tzutil command...");

                    var processInfo = new ProcessStartInfo
                    {
                        FileName = "tzutil",
                        Arguments = "/s \"Iran Standard Time\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = processInfo };
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null) outputBuilder.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null) errorBuilder.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    await process.WaitForExitAsync(cancellationToken);
                    
                    if (process.ExitCode == 0)
                    {
                        logs.Add("✓ Successfully set Iran Standard Time using tzutil");
                        success = true;
                    }
                    else
                    {
                        logs.Add($"⚠ tzutil failed with exit code {process.ExitCode}");
                        var error = errorBuilder.ToString().Trim();
                        if (!string.IsNullOrEmpty(error))
                        {
                            logs.Add($"tzutil error: {error}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    logs.Add("⚠ tzutil command cancelled due to timeout");
                }
                catch (Exception ex)
                {
                    logs.Add($"⚠ tzutil fallback failed: {ex.Message}");
                }
            }

            // Method 3: Registry fallback for DST disable with timeout
            if (success)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logs.Add("Disabling Daylight Saving Time through registry...");

                    var regProcessInfo = new ProcessStartInfo
                    {
                        FileName = "reg",
                        Arguments = "add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\TimeZoneInformation\" /v DynamicDaylightTimeDisabled /t REG_DWORD /d 1 /f",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var regProcess = new Process { StartInfo = regProcessInfo };
                    regProcess.Start();
                    await regProcess.WaitForExitAsync(cancellationToken);
                    if (regProcess.ExitCode == 0)
                    {
                        logs.Add("✓ Successfully disabled Daylight Saving Time");
                    }
                    else
                    {
                        logs.Add("⚠ Failed to disable DST, but timezone was set");
                    }
                }
                catch (OperationCanceledException)
                {
                    logs.Add("⚠ Registry DST disable cancelled due to timeout");
                }
                catch (Exception ex)
                {
                    logs.Add($"⚠ Registry DST disable failed: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = success,
                Message = success ? "Iran timezone set successfully" : "Failed to set Iran timezone",
                Output = string.Join("\n", logs)
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timezone setting operation cancelled due to timeout");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = "Timezone setting operation timed out after 25 seconds",
                Output = "Operation was cancelled due to timeout. Please try again."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting Iran timezone");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = $"Error setting timezone: {ex.Message}",
                Output = ""
            });
        }
    }

    private async Task<(bool success, string message)> SetSystemTimeZoneUsingDotNet(TimeZoneInfo timeZone)
    {
#if WINDOWS
        try
        {
            // Use Windows API through P/Invoke to set system timezone
            // This requires administrative privileges

            var tzi = new TIME_ZONE_INFORMATION
            {
                Bias = (int)-timeZone.BaseUtcOffset.TotalMinutes,
                StandardName = timeZone.StandardName.ToCharArray(),
                DaylightName = timeZone.DaylightName.ToCharArray(),
                StandardDate = new SYSTEMTIME(),
                DaylightDate = new SYSTEMTIME()
            };

            // Disable daylight saving time
            tzi.StandardDate.wMonth = 0;
            tzi.DaylightDate.wMonth = 0;

            bool result = SetTimeZoneInformation(ref tzi);

            if (result)
            {
                return (true, $"Successfully set timezone to {timeZone.DisplayName}");
            }
            else
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return (false, $"SetTimeZoneInformation failed with error code: {error}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Exception in SetSystemTimeZoneUsingDotNet: {ex.Message}");
        }
#else
        return (false, "Setting system timezone is only supported on Windows");
#endif
    }

    private async Task<(bool success, string message)> SetSystemTimeZoneUsingDotNetWithTimeout(TimeZoneInfo timeZone, TimeSpan timeout, CancellationToken cancellationToken)
    {
#if WINDOWS
        try
        {
            // Use Windows API through P/Invoke to set system timezone
            // This requires administrative privileges

            var tzi = new TIME_ZONE_INFORMATION
            {
                Bias = (int)-timeZone.BaseUtcOffset.TotalMinutes,
                StandardName = timeZone.StandardName.ToCharArray(),
                DaylightName = timeZone.DaylightName.ToCharArray(),
                StandardDate = new SYSTEMTIME(),
                DaylightDate = new SYSTEMTIME()
            };

            // Disable daylight saving time
            tzi.StandardDate.wMonth = 0;
            tzi.DaylightDate.wMonth = 0;

            bool result = SetTimeZoneInformation(ref tzi);

            if (result)
            {
                return (true, $"Successfully set timezone to {timeZone.DisplayName}");
            }
            else
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return (false, $"SetTimeZoneInformation failed with error code: {error}");
            }
        }
        catch (OperationCanceledException)
        {
            return (false, $"SetTimeZoneInformation operation timed out after {timeout.TotalSeconds} seconds");
        }
        catch (Exception ex)
        {
            return (false, $"Exception in SetSystemTimeZoneUsingDotNetWithTimeout: {ex.Message}");
        }
#else
        return (false, "Setting system timezone is only supported on Windows");
#endif
    }

    private async Task<string> FixDateTime()
    {
        try
        {
            var logs = new List<string>();
            bool overallSuccess = false;

            // Add overall timeout protection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30 second overall timeout
            var cancellationToken = cts.Token;

            // Test connectivity to time servers with timeout
            var timeServers = new[] { "mtnirancell.ir", "ir.pool.ntp.org", "pool.ntp.org", "time.windows.com" };
            string? workingServer = null;

            logs.Add("Testing connectivity to time servers (with 30s overall timeout)...");

            foreach (var server in timeServers)
            {
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(server, 3000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        logs.Add($"✓ Ping to {server} succeeded");
                        workingServer = server;
                        break;
                    }
                    else
                    {
                        logs.Add($"✗ Ping to {server} failed: {reply.Status}");
                    }
                }
                catch (OperationCanceledException)
                {
                    logs.Add($"✗ Ping to {server} cancelled");
                }
                catch (Exception ex)
                {
                    logs.Add($"✗ Ping to {server} error: {ex.Message}");
                }
            }

            if (workingServer != null)
            {
                logs.Add($"Using {workingServer} for time synchronization");

                // Method 1: Use .NET NTP client with timeout
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logs.Add("Attempting time sync using .NET NTP client...");
                    
                    var ntpTime = await GetNtpTimeWithTimeout(workingServer, TimeSpan.FromSeconds(10), cancellationToken);

                    if (ntpTime.HasValue)
                    {
                        var result = await SetSystemTimeWithTimeout(ntpTime.Value, TimeSpan.FromSeconds(5), cancellationToken);
                        if (result.success)
                        {
                            logs.Add($"✓ Time synchronized successfully to {ntpTime.Value:yyyy-MM-dd HH:mm:ss}");
                            overallSuccess = true;
                        }
                        else
                        {
                            logs.Add($"⚠ Failed to set system time: {result.message}");
                        }
                    }
                    else
                    {
                        logs.Add("⚠ Failed to get time from NTP server");
                    }
                }
                catch (OperationCanceledException)
                {
                    logs.Add("⚠ NTP sync cancelled due to timeout");
                }
                catch (Exception ex)
                {
                    logs.Add($"⚠ .NET NTP sync failed: {ex.Message}");
                }

                // Method 2: Fallback to w32tm command with timeout
                if (!overallSuccess)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        logs.Add("Falling back to w32tm command...");

                        var processInfo = new ProcessStartInfo
                        {
                            FileName = "w32tm",
                            Arguments = $"/config /manualpeerlist:\"{workingServer}\" /syncfromflags:manual /reliable:yes /update",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = new Process { StartInfo = processInfo };
                        var outputBuilder = new StringBuilder();
                        var errorBuilder = new StringBuilder();

                        process.OutputDataReceived += (sender, e) =>
                        {
                            if (e.Data != null) outputBuilder.AppendLine(e.Data);
                        };
                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (e.Data != null) errorBuilder.AppendLine(e.Data);
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        
                        // Wait for process with timeout
                        await process.WaitForExitAsync(cancellationToken);
                        if (process.ExitCode == 0)
                        {
                            logs.Add("✓ w32tm config successful");

                            // Now force a sync with timeout
                            cancellationToken.ThrowIfCancellationRequested();
                            var syncProcessInfo = new ProcessStartInfo
                            {
                                FileName = "w32tm",
                                Arguments = "/resync /force",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };

                            using var syncProcess = new Process { StartInfo = syncProcessInfo };
                            syncProcess.Start();
                            await syncProcess.WaitForExitAsync(cancellationToken);
                            if (syncProcess.ExitCode == 0)
                            {
                                logs.Add("✓ Time synchronization completed using w32tm");
                                overallSuccess = true;
                            }
                            else
                            {
                                logs.Add("⚠ w32tm sync failed");
                            }
                        }
                        else
                        {
                            logs.Add($"⚠ w32tm config failed with exit code {process.ExitCode}");
                            var error = errorBuilder.ToString().Trim();
                            if (!string.IsNullOrEmpty(error))
                            {
                                logs.Add($"w32tm error: {error}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logs.Add("⚠ w32tm operations cancelled due to timeout");
                    }
                    catch (Exception ex)
                    {
                        logs.Add($"⚠ w32tm fallback failed: {ex.Message}");
                    }
                }
            }
            else
            {
                logs.Add("✗ No time servers are reachable for synchronization");
            }

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = overallSuccess,
                Message = overallSuccess ? "Date/time synchronized successfully" : "Failed to synchronize date/time",
                Output = string.Join("\n", logs)
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DateTime fix operation cancelled due to timeout");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = "DateTime fix operation timed out after 30 seconds",
                Output = "Operation was cancelled due to timeout. Please try again or check network connectivity."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing date/time");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = $"Error fixing date/time: {ex.Message}",
                Output = ""
            });
        }
    }

    private async Task<DateTime?> GetNtpTime(string server)
    {
        try
        {
            var ntpData = new byte[48];
            ntpData[0] = 0x1B; // LI = 0 (no warning), VN = 3 (IPv4 only), Mode = 3 (Client Mode)

            var addresses = await System.Net.Dns.GetHostAddressesAsync(server);
            var ipEndPoint = new System.Net.IPEndPoint(addresses[0], 123);

            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.ReceiveTimeout = 3000;
            socket.SendTimeout = 3000;

            await socket.ConnectAsync(ipEndPoint);
            await socket.SendAsync(ntpData, System.Net.Sockets.SocketFlags.None);

            var buffer = new byte[48];
            var received = await socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None);

            if (received < 48) return null;

            // Extract timestamp from bytes 40-43 (transmit timestamp)
            ulong intPart = (ulong)buffer[40] << 24 | (ulong)buffer[41] << 16 | (ulong)buffer[42] << 8 | (ulong)buffer[43];
            ulong fracPart = (ulong)buffer[44] << 24 | (ulong)buffer[45] << 16 | (ulong)buffer[46] << 8 | (ulong)buffer[47];

            var milliseconds = (intPart * 1000) + ((fracPart * 1000) / 0x100000000L);
            var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);

            return networkDateTime.ToLocalTime();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get NTP time from {server}");
            return null;
        }
    }

    /// <summary>
    /// Get NTP time with timeout protection to prevent hanging
    /// </summary>
    private async Task<DateTime?> GetNtpTimeWithTimeout(string server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var ntpData = new byte[48];
            ntpData[0] = 0x1B; // LI = 0 (no warning), VN = 3 (IPv4 only), Mode = 3 (Client Mode)

            var addresses = await System.Net.Dns.GetHostAddressesAsync(server, timeoutCts.Token);
            var ipEndPoint = new System.Net.IPEndPoint(addresses[0], 123);

            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            socket.SendTimeout = (int)timeout.TotalMilliseconds;

            await socket.ConnectAsync(ipEndPoint, timeoutCts.Token);
            await socket.SendAsync(ntpData, System.Net.Sockets.SocketFlags.None, timeoutCts.Token);

            var buffer = new byte[48];
            var received = await socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None, timeoutCts.Token);

            if (received < 48) return null;

            // Extract timestamp from bytes 40-43 (transmit timestamp)
            ulong intPart = (ulong)buffer[40] << 24 | (ulong)buffer[41] << 16 | (ulong)buffer[42] << 8 | (ulong)buffer[43];
            ulong fracPart = (ulong)buffer[44] << 24 | (ulong)buffer[45] << 16 | (ulong)buffer[46] << 8 | (ulong)buffer[47];

            var milliseconds = (intPart * 1000) + ((fracPart * 1000) / 0x100000000L);
            var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);

            return networkDateTime.ToLocalTime();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"NTP time fetch from {server} cancelled due to timeout ({timeout.TotalSeconds}s)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get NTP time from {server}");
            return null;
        }
    }

    private async Task<(bool success, string message)> SetSystemTime(DateTime time)
    {
#if WINDOWS
        try
        {
            var st = new SYSTEMTIME
            {
                wYear = (short)time.Year,
                wMonth = (short)time.Month,
                wDayOfWeek = (short)time.DayOfWeek,
                wDay = (short)time.Day,
                wHour = (short)time.Hour,
                wMinute = (short)time.Minute,
                wSecond = (short)time.Second,
                wMilliseconds = (short)time.Millisecond
            };

            bool result = SetLocalTime(ref st);

            if (result)
            {
                return (true, $"System time set to {time:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return (false, $"SetLocalTime failed with error code: {error}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Exception in SetSystemTime: {ex.Message}");
        }
#else
        return (false, "Setting system time is only supported on Windows");
#endif
    }

    /// <summary>
    /// Set system time with timeout protection to prevent hanging
    /// </summary>
    private async Task<(bool success, string message)> SetSystemTimeWithTimeout(DateTime time, TimeSpan timeout, CancellationToken cancellationToken)
    {
#if WINDOWS
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var st = new SYSTEMTIME
            {
                wYear = (short)time.Year,
                wMonth = (short)time.Month,
                wDayOfWeek = (short)time.DayOfWeek,
                wDay = (short)time.Day,
                wHour = (short)time.Hour,
                wMinute = (short)time.Minute,
                wSecond = (short)time.Second,
                wMilliseconds = (short)time.Millisecond
            };

            // Run the Windows API call in a task with timeout
            var setTimeTask = Task.Run(() => SetLocalTime(ref st), timeoutCts.Token);
            
            try
            {
                bool result = await setTimeTask;
                
                if (result)
                {
                    return (true, $"System time set to {time:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return (false, $"SetLocalTime failed with error code: {error}");
                }
            }
            catch (OperationCanceledException)
            {
                return (false, $"SetLocalTime operation timed out after {timeout.TotalSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Exception in SetSystemTimeWithTimeout: {ex.Message}");
        }
#else
        return (false, "Setting system time is only supported on Windows");
#endif
    }

#if WINDOWS
    // P/Invoke declarations for Windows API
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetLocalTime(ref SYSTEMTIME time);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetTimeZoneInformation(ref TIME_ZONE_INFORMATION lpTimeZoneInformation);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public short wYear;
        public short wMonth;
        public short wDayOfWeek;
        public short wDay;
        public short wHour;
        public short wMinute;
        public short wSecond;
        public short wMilliseconds;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }
#endif

    private bool IsRunningElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);

                // Check if running as Administrator
                bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

                // Also check if running as SYSTEM account (Windows Services typically run as SYSTEM)
                // SYSTEM account has even higher privileges than Administrator
                bool isSystem = identity.IsSystem;

                // Log for debugging
                _logger.LogDebug($"Windows privilege check - User: {identity.Name}, IsAdmin: {isAdmin}, IsSystem: {isSystem}");

                return isAdmin || isSystem;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Windows privileges");
                return false;
            }
        }
        else
        {
            // On Linux, check if running as root
            return Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
        }
    }

    /// <summary>
    /// Get a safe working directory that exists, with fallback options to prevent "path not found" errors
    /// </summary>
    private string GetSafeWorkingDirectory(string scriptPath)
    {
        try
        {
            // Strategy 1: Use the script's directory if it exists
            var scriptDir = Path.GetDirectoryName(scriptPath);
            if (!string.IsNullOrEmpty(scriptDir) && Directory.Exists(scriptDir))
            {
                return scriptDir;
            }

            // Strategy 2: Use system temp directory
            var tempPath = Path.GetTempPath();
            if (Directory.Exists(tempPath))
            {
                return tempPath;
            }

            // Strategy 3: Platform-specific fallbacks
            if (OperatingSystem.IsWindows())
            {
                var windowsFallbacks = new[]
                {
                    @"C:\Windows\Temp",
                    @"C:\Temp",
                    @"C:\Windows\System32",
                    @"C:\"
                };

                foreach (var fallback in windowsFallbacks)
                {
                    if (Directory.Exists(fallback))
                    {
                        _logger.LogWarning($"Using fallback working directory: {fallback}");
                        return fallback;
                    }
                }
            }
            else
            {
                var linuxFallbacks = new[] { "/tmp", "/var/tmp", "/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };

                foreach (var fallback in linuxFallbacks)
                {
                    if (Directory.Exists(fallback))
                    {
                        _logger.LogWarning($"Using fallback working directory: {fallback}");
                        return fallback;
                    }
                }
            }

            // Strategy 4: Last resort - current directory
            var currentDir = Environment.CurrentDirectory;
            _logger.LogError($"All working directory options failed, using current directory: {currentDir}");
            return currentDir;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error determining safe working directory, using current directory");
            return Environment.CurrentDirectory;
        }
    }

    private void StartUptimeMonitoring()
    {
        // Start monitoring timer (check every 30 minutes)
        _uptimeMonitorTimer = new Timer(async _ => await CheckUptimeAndShowWarning(),
            null, TimeSpan.Zero, TimeSpan.FromMinutes(30));
        _logger.LogInformation("Uptime monitoring started (30-minute intervals)");
    }

    private void StartTokenRefreshMonitoring()
    {
        // Start token refresh timer (every 10 minutes)
        _tokenRefreshTimer = new Timer(async _ => await RefreshTokenAsync(),
            null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        _logger.LogInformation("Token refresh monitoring started (10-minute intervals)");
    }

    private async Task CheckUptimeAndShowWarning()
    {
        try
        {
            await _uptimeMonitor.CheckUptimeAndShowWarning();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in uptime monitoring");
        }
    }

    private async Task RefreshTokenAsync()
    {
        try
        {
            _logger.LogInformation("Attempting to refresh authentication token...");
            
            // Check if we need to refresh the token
            if (await ShouldRefreshTokenAsync())
            {
                var newToken = await FetchNewTokenAsync();
                if (newToken != null)
                {
                    await SaveTokenToFileAsync(newToken);
                    _logger.LogInformation("Token refreshed and saved successfully");
                }
                else
                {
                    _logger.LogWarning("Failed to fetch new token");
                }
            }
            else
            {
                _logger.LogInformation("Token is still valid, no refresh needed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing authentication token");
        }
    }

    private async Task<bool> ShouldRefreshTokenAsync()
    {
        try
        {
            if (!File.Exists(_tokenFilePath))
            {
                _logger.LogInformation("No token file found, need to fetch new token");
                return true;
            }

            var tokenData = await ReadTokenFromFileAsync();
            if (tokenData == null)
            {
                _logger.LogInformation("Token file is invalid, need to fetch new token");
                return true;
            }

            // Check if token is older than 8 hours (refresh before 24-hour expiration)
            var tokenAge = DateTime.UtcNow - tokenData.Timestamp;
            if (tokenAge.TotalHours > 8)
            {
                _logger.LogInformation($"Token is {tokenAge.TotalHours:F1} hours old, need to refresh");
                return true;
            }

            _logger.LogInformation($"Token is {tokenAge.TotalHours:F1} hours old, still valid");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if token should be refreshed");
            return true; // Refresh on error to be safe
        }
    }

    private async Task<TokenData?> FetchNewTokenAsync()
    {
        try
        {
            _logger.LogInformation("Fetching new authentication token...");
            
            // Get credentials from environment or use defaults
            var username = Environment.GetEnvironmentVariable("username") ?? "admin";
            var password = Environment.GetEnvironmentVariable("password") ?? "Admin@3123456";
            
            var loginData = new
            {
                username = username,
                password = password
            };

            var jsonContent = JsonSerializer.Serialize(loginData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Add headers to match Python script
            var request = new HttpRequestMessage(HttpMethod.Post, "https://selfcare.mtnirancell.ir/api/auth/login")
            {
                Content = content
            };

            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "en,fa;q=0.9,en-US;q=0.8,en-CA;q=0.7,en-GB;q=0.6,en-GB-oxendict;q=0.5");
            request.Headers.Add("DNT", "1");
            request.Headers.Add("Origin", "https://selfcare.mtnirancell.ir:4433");
            request.Headers.Add("Priority", "u=1, i");
            request.Headers.Add("Referer", "https://selfcare.mtnirancell.ir:4433/");
            request.Headers.Add("Sec-Ch-Ua", "\"Not;A=Brand\";v=\"99\", \"Google Chrome\";v=\"139\", \"Chromium\";v=\"139\"");
            request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
            request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-site");
            request.Headers.Add("Sec-Gpc", "1");

            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Login response received successfully");
                
                var loginResponse = JsonSerializer.Deserialize<LoginResponse>(responseContent);
                if (loginResponse?.Success == true && loginResponse.Data?.Token != null)
                {
                    var tokenData = new TokenData
                    {
                        AccessToken = loginResponse.Data.Token,
                        Timestamp = DateTime.UtcNow,
                        Source = "MTN Irancell Selfcare API (C# Service)"
                    };
                    
                    _logger.LogInformation($"New token fetched successfully: {tokenData.AccessToken[..20]}...");
                    return tokenData;
                }
                else
                {
                    _logger.LogWarning("Login response indicates failure or missing token");
                    _logger.LogDebug($"Response content: {responseContent}");
                }
            }
            else
            {
                _logger.LogWarning($"Login request failed with status: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug($"Error response: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching new token");
        }

        return null;
    }

    private async Task SaveTokenToFileAsync(TokenData tokenData)
    {
        try
        {
            var jsonContent = JsonSerializer.Serialize(tokenData, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            await File.WriteAllTextAsync(_tokenFilePath, jsonContent, Encoding.UTF8);
            _logger.LogInformation($"Token saved to file: {_tokenFilePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving token to file");
        }
    }

    private async Task<TokenData?> ReadTokenFromFileAsync()
    {
        try
        {
            var content = await File.ReadAllTextAsync(_tokenFilePath, Encoding.UTF8);
            var tokenData = JsonSerializer.Deserialize<TokenData>(content, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            return tokenData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading token from file");
            return null;
        }
    }

    private async Task<string> HandleUptimeCheck()
    {
        try
        {
            var uptime = _uptimeMonitor.GetSystemUptime();
            var uptimeInfo = new
            {
                TotalSeconds = uptime.TotalSeconds,
                Days = uptime.Days,
                Hours = uptime.Hours,
                Minutes = uptime.Minutes,
                FormattedUptime = _uptimeMonitor.GetFormattedUptime(),
                ShouldShowWarning = _uptimeMonitor.ShouldShowWarning()
            };

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = true,
                Message = "Uptime information retrieved",
                Output = JsonSerializer.Serialize(uptimeInfo)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting uptime information");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<string> HandleShowRebootWarning()
    {
        try
        {
            var result = await _uptimeMonitor.ShowRebootWarningDialog();

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = true,
                Message = "Reboot warning dialog shown",
                Output = JsonSerializer.Serialize(new { UserChoice = result })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing reboot warning");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<string> HandleGetUptimeStatus()
    {
        try
        {
            var status = _uptimeMonitor.GetUptimeStatus();

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = true,
                Message = "Uptime status retrieved",
                Output = JsonSerializer.Serialize(status)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting uptime status");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<string> HandleRefreshToken()
    {
        try
        {
            _logger.LogInformation("Manual token refresh requested via service call");
            
            var newToken = await FetchNewTokenAsync();
            if (newToken != null)
            {
                await SaveTokenToFileAsync(newToken);
                
                var result = new
                {
                    Success = true,
                    Token = newToken.AccessToken[..20] + "...",
                    Timestamp = newToken.Timestamp,
                    Source = newToken.Source
                };
                
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = true,
                    Message = "Token refreshed successfully",
                    Output = JsonSerializer.Serialize(result)
                });
            }
            else
            {
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "Failed to fetch new token",
                    Output = "Check service logs for details"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in manual token refresh");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    private async Task<string> HandleGetTokenStatus()
    {
        try
        {
            if (!File.Exists(_tokenFilePath))
            {
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "Token file not found",
                    Output = "Token file not found. Please ensure the service is running and has access to the token file."
                });
            }

            var tokenData = await ReadTokenFromFileAsync();
            if (tokenData == null)
            {
                return JsonSerializer.Serialize(new ServiceResponse
                {
                    Success = false,
                    Message = "Token file is invalid",
                    Output = "Token file is invalid or corrupted. Please delete it and restart the service."
                });
            }

            var tokenAge = DateTime.UtcNow - tokenData.Timestamp;
            var hoursOld = tokenAge.TotalHours;

            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = true,
                Message = "Token status retrieved",
                Output = JsonSerializer.Serialize(new
                {
                    AccessToken = tokenData.AccessToken[..20] + "...",
                    Timestamp = tokenData.Timestamp,
                    Source = tokenData.Source,
                    HoursOld = hoursOld
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting token status");
            return JsonSerializer.Serialize(new ServiceResponse
            {
                Success = false,
                Message = ex.Message,
                Output = ""
            });
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Selfcare Service stopping...");

        _uptimeMonitorTimer?.Dispose();
        _tokenRefreshTimer?.Dispose();
        _tcpListener?.Stop();

        // Clean up port file
        try
        {
            var portFilePath = GetPortFilePath();
            if (File.Exists(portFilePath))
            {
                File.Delete(portFilePath);
                _logger.LogInformation("Port file cleaned up");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up port file");
        }

        await base.StopAsync(cancellationToken);
    }
}

// Data models for IPC
public class ServiceRequest
{
    public string Type { get; set; } = string.Empty;
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? Data { get; set; }
}

public class ServiceResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
}

public class TokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
}

// Login response models for token fetching
public class LoginResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public LoginData? Data { get; set; }
}

public class LoginData
{
    public string Token { get; set; } = string.Empty;
    public AdminData Admin { get; set; } = new AdminData();
}

public class AdminData
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
