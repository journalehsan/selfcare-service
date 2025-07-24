# Selfcare Service

A .NET Core background service that provides privileged task execution and system monitoring for the Selfcare application.

## Overview

This service runs as a background service (Windows Service, Linux systemd, or standalone) and provides:

- **Privileged Command Execution**: Execute system commands with appropriate privileges
- **System Status Monitoring**: Retrieve system information and process status
- **Cross-Platform Support**: Works on Windows, Linux, and macOS
- **TCP Communication**: Communicates with Rust clients via TCP sockets

## Architecture

The service uses TCP sockets for inter-process communication (IPC) with the main Selfcare Rust application. It writes its port number to a temporary file (`/tmp/selfcare_port.txt` on Unix, `%TEMP%\selfcare_port.txt` on Windows) that clients can read to establish connections.

## Features

### Communication Protocol
- **JSON-based messaging**: Request/response format using JSON serialization
- **Port discovery**: Automatic port allocation (8080-8099 range) with file-based discovery
- **Secure permissions**: Port file secured with 600 permissions on Unix systems

### Supported Operations
1. **RunCommand**: Execute system commands with arguments
2. **GetSystemStatus**: Retrieve system information (platform, process ID, working directory, elevation status)
3. **CheckPrivileges**: Check if the service is running with elevated privileges

### Response Format
```json
{
  "Success": true/false,
  "Message": "Human-readable message",
  "Output": "Command output or data",
  "ExitCode": 0 (optional, for commands)
}
```

## Building and Running

### Prerequisites
- .NET 9.0 SDK or later
- Windows, Linux, or macOS

### Development
```bash
# Clone the repository
git clone https://github.com/journalehsan/selfcare-service.git
cd selfcare-service/SelfcareService

# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run in development mode
dotnet run
```

### Production Deployment

#### Linux (systemd)
```bash
# Publish for Linux
dotnet publish -r linux-x64 --self-contained -c Release

# Install as systemd service
sudo cp bin/Release/net9.0/linux-x64/publish/SelfcareService /usr/local/bin/
sudo cp selfcare-service.service /etc/systemd/system/
sudo systemctl enable selfcare-service
sudo systemctl start selfcare-service
```

#### Windows (Windows Service)
```cmd
# Publish for Windows
dotnet publish -r win-x64 --self-contained -c Release

# Install as Windows Service
sc create SelfcareService binPath="C:\path\to\SelfcareService.exe" start=auto
sc start SelfcareService
```

## Configuration

The service automatically:
- Finds an available port in the range 8080-8099
- Creates the port file with secure permissions
- Logs all operations with appropriate log levels
- Handles graceful shutdown with cleanup

## Security Considerations

- The service only accepts connections from localhost (127.0.0.1)
- Port file permissions are set to 600 (owner read/write only) on Unix systems
- All command execution is logged for audit purposes
- Error messages are sanitized to prevent information disclosure

## Integration with Rust Client

The Rust client uses the `run_command` module to communicate with this service:

```rust
use selfcare::core::run_command::{run_privileged_command, is_service_running};

// Check if service is running
if is_service_running() {
    // Execute a command
    let result = run_privileged_command("ls", Some("-la /tmp"))?;
    println!("Output: {}", result.output);
}
```

## Logging

The service uses structured logging with the following levels:
- **Information**: Service lifecycle, successful operations
- **Warning**: Non-critical issues (e.g., port conflicts)
- **Error**: Command execution failures, communication errors

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## License

This project is part of the Selfcare application suite.
