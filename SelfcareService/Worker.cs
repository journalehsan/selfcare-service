using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using AudioSwitcher.AudioApi.CoreAudio;

namespace SelfcareService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private TcpListener? _tcpListener;
    private int _port = 8080;
    private SecureAuthenticator? _secureAuth;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
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
            
            // Also write to /tmp for compatibility with regular users
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
            
            // Set secure permissions on Unix systems
            if (!OperatingSystem.IsWindows())
            {
                var chmod = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 {portFilePath}",
                    UseShellExecute = false
                };
                using var process = Process.Start(chmod);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
                
                // Make tmp file readable by all users
                try
                {
                    var chmodTmp = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"644 {tmpPortFile}",
                        UseShellExecute = false
                    };
                    using var tmpProcess = Process.Start(chmodTmp);
                    if (tmpProcess != null)
                    {
                        await tmpProcess.WaitForExitAsync();
                    }
                }
                catch (Exception chmodEx)
                {
                    _logger.LogWarning(chmodEx, $"Failed to set permissions on {tmpPortFile}");
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
                    
                    // Extract auth key and actual request
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

    private async Task<string> ProcessRequest(string request)
    {
        try
        {
            var serviceRequest = JsonSerializer.Deserialize<ServiceRequest>(request);
            
            return serviceRequest?.Type switch
            {
                "RunCommand" => await HandleRunCommand(serviceRequest.Command, serviceRequest.Arguments),
                "GetSystemStatus" => await HandleSystemStatus(),
                "CheckPrivileges" => await HandleCheckPrivileges(),
                "AudioMethod" => await HandleAudioControl(serviceRequest.Command, serviceRequest.Arguments),
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
            var processInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) => {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            _logger.LogInformation($"Command '{command} {arguments}' executed with exit code {process.ExitCode}");

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
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Output volume retrieved",
                        Output = playbackDevice.Volume.ToString()
                    });

                case "GetInputVolume":
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = true,
                        Message = "Input volume retrieved",
                        Output = captureDevice.Volume.ToString()
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
                    if (double.TryParse(arguments, out double outputVolume) && outputVolume >= 0 && outputVolume <= 100)
                    {
                        await playbackDevice.SetVolumeAsync(outputVolume);
                        return JsonSerializer.Serialize(new ServiceResponse
                        {
                            Success = true,
                            Message = $"Output volume set to {outputVolume}%",
                            Output = outputVolume.ToString()
                        });
                    }
                    return JsonSerializer.Serialize(new ServiceResponse
                    {
                        Success = false,
                        Message = "Invalid volume level. Must be between 0 and 100.",
                        Output = ""
                    });

                case "SetInputVolume":
                    if (double.TryParse(arguments, out double inputVolume) && inputVolume >= 0 && inputVolume <= 100)
                    {
                        await captureDevice.SetVolumeAsync(inputVolume);
                        return JsonSerializer.Serialize(new ServiceResponse
                        {
                            Success = true,
                            Message = $"Input volume set to {inputVolume}%",
                            Output = inputVolume.ToString()
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
    }

    private bool IsRunningElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        else
        {
            // On Linux, check if running as root
            return Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Selfcare Service stopping...");
        
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
