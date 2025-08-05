# SelfCare Service Authentication

This document describes the basic authentication system implemented for the SelfCare service to provide simple security for command execution.

## Overview

The authentication system uses a simple fixed token-based mechanism for basic security. This is designed for development and testing environments where simplicity is preferred over complex cryptographic security.

**Current Implementation:** Fixed token authentication using `"selfcare:SelfCare@#2025"`

## How It Works

### Authentication Method

The service uses a fixed authentication token for all requests. This provides basic protection while keeping the implementation simple and avoiding complex time-based or cryptographic authentication mechanisms.

### Protocol Format

Communication between clients and the service follows a two-line protocol:

1. **Line 1:** Authentication token
2. **Line 2:** JSON service request

### Authentication Flow

1. **Client Request Format:**
   ```
   selfcare:SelfCare@#2025
   {"Type":"RunCommand","Command":"echo","Arguments":"Hello World"}
   ```

2. **Service Validation:**
   - Extract authentication token from first line
   - Compare with expected token: `"selfcare:SelfCare@#2025"`
   - Parse JSON request from second line if authentication succeeds
   - Process request and return JSON response

## Implementation Details

### C# Service (Worker.cs)

```csharp
private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
{
    // Read request data
    var buffer = new byte[4096];
    var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
    var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
    
    // Parse authentication and request
    var lines = request.Split('\n', 2);
    if (lines.Length < 2)
    {
        _logger.LogWarning("Request missing authentication key");
        return;
    }
    
    var clientToken = lines[0].Trim();
    var actualRequest = lines[1];
    
    // Simple basic authentication
    string expectedToken = "selfcare:SelfCare@#2025";
    if (clientToken != expectedToken)
    {
        _logger.LogWarning($"Basic authentication failed. Expected: '{expectedToken}', Received: '{clientToken}'");
        return;
    }
    
    // Process authenticated request
    var response = await ProcessRequest(actualRequest);
    var responseBytes = Encoding.UTF8.GetBytes(response);
    await stream.WriteAsync(responseBytes, cancellationToken);
}
```

### Rust Client Example

```rust
use std::io::{Read, Write};
use std::net::TcpStream;
use serde_json;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Connect to service
    let mut stream = TcpStream::connect("127.0.0.1:8080")?;
    
    // Create request
    let auth_token = "selfcare:SelfCare@#2025";
    let service_request = serde_json::json!({
        "Type": "RunCommand",
        "Command": "echo",
        "Arguments": "Hello from Rust client!"
    });
    
    let service_request_json = serde_json::to_string(&service_request)?;
    let full_request = format!("{}\n{}", auth_token, service_request_json);
    
    // Send request
    stream.write_all(full_request.as_bytes())?;
    stream.flush()?;
    
    // Read response
    let mut buffer = Vec::new();
    stream.read_to_end(&mut buffer)?;
    let response = String::from_utf8(buffer)?;
    
    println!("Response: {}", response);
    Ok(())
}
```

## Service Request Types

The service supports the following request types:

### RunCommand
```json
{
  "Type": "RunCommand",
  "Command": "echo",
  "Arguments": "Hello World"
}
```

### GetSystemStatus
```json
{
  "Type": "GetSystemStatus"
}
```

### CheckPrivileges
```json
{
  "Type": "CheckPrivileges"
}
```

## Service Response Format

All service responses follow this JSON format:

```json
{
  "Success": true,
  "Message": "Command executed successfully",
  "Output": "Hello World\n",
  "ExitCode": 0
}
```

## Security Considerations

### Strengths
- **Simple implementation:** Easy to understand and debug
- **Local-only:** Service typically runs on localhost
- **Basic protection:** Prevents accidental unauthorized access
- **No complex dependencies:** No cryptographic libraries required

### Limitations
- **Fixed token:** Same token used for all requests
- **No encryption:** Token transmitted in plain text
- **No expiration:** Token never changes unless manually updated
- **Basic security:** Not suitable for production environments with security requirements

### Recommendations
- **Development/Testing Only:** This authentication method is designed for development and testing
- **Trusted environments:** Use only in controlled, trusted network environments
- **Network isolation:** Run service on localhost or isolated networks
- **Regular updates:** Consider changing the token periodically
- **Enhanced security:** Implement proper authentication for production use

## Usage

### Testing Authentication
```bash
# Using the test client
cd /path/to/rust-client
cargo build --release
./target/release/test-run-command
```

### Expected Behavior
- ✅ Requests with correct token `"selfcare:SelfCare@#2025"` succeed
- ❌ Requests without authentication token are rejected
- ❌ Requests with incorrect authentication tokens are rejected
- ❌ Malformed requests (missing second line) are rejected

## Troubleshooting

### Authentication Failures
1. **Check token:** Ensure exact match with `"selfcare:SelfCare@#2025"`
2. **Verify format:** Ensure request follows two-line protocol format
3. **Review logs:** Service logs authentication attempts with details
4. **Check connection:** Verify service is running and accessible

### Common Issues
- **Token mismatch:** Case-sensitive token comparison
- **Protocol format:** Missing newline between token and JSON request
- **JSON format:** Malformed JSON in service request
- **Network connectivity:** Service not accessible on expected port

## Log Messages

### Successful Authentication
```
[INFO] Client connected
[INFO] Received request from client
[INFO] Authentication successful
[INFO] Command 'echo Hello World' executed with exit code 0
```

### Failed Authentication
```
[INFO] Client connected
[INFO] Received request from client
[WARN] Basic authentication failed. Expected: 'selfcare:SelfCare@#2025', Received: 'wrong_token'
```

### Missing Authentication
```
[INFO] Client connected
[INFO] Received request from client
[WARN] Request missing authentication key
```
