using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace SelfcareService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private TcpListener? _tcpListener;
    private int _port = 8080;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Selfcare Service starting...");

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
        return OperatingSystem.IsWindows() 
            ? Path.Combine(Path.GetTempPath(), "selfcare_port.txt")
            : "/tmp/selfcare_port.txt";
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
                    _logger.LogInformation($"Received request: {request}");
                    
                    var response = await ProcessRequest(request);
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
