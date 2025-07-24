# SelfCare Service Authentication

This document describes the time-based authentication system implemented for the SelfCare service to provide basic security for command execution.

## Overview

The authentication system uses a simple time-based key generation mechanism that combines:
- Machine hostname
- Current username  
- Current hour (in HH00 format, e.g., "0300" for 3 AM, "2300" for 11 PM)

This provides a rolling authentication window where keys are valid for one hour at a time.

## How It Works

### Key Generation Algorithm

Both the client (Rust) and service (C#) generate the same authentication key using:

1. **Data Collection:**
   - Hostname: `Dns.GetHostName()` (C#) / `whoami::hostname()` (Rust)
   - Username: `Environment.UserName` (C#) / `whoami::username()` (Rust)
   - Time: `DateTime.UtcNow.ToString("HH00")` (C#) / `chrono::Utc::now().format("%H00")` (Rust)

2. **Key Construction:**
   - Concatenate: `hostname + username + time`
   - Example: `"MyComputer" + "john" + "1400"` = `"MyComputerjohn1400"`

3. **Hashing:**
   - SHA256 hash of the concatenated string
   - Base64 encode the hash result

### Authentication Flow

1. **Client Request:**
   ```
   [AUTH_KEY]
   [JSON_REQUEST]
   ```

2. **Service Validation:**
   - Extract auth key from first line
   - Generate expected key using same algorithm
   - Compare keys (exact match required)
   - Process request only if keys match

### Security Features

- **Time-based rotation:** Keys change every hour automatically
- **Machine-specific:** Keys are unique per hostname/username combination
- **Non-predictable:** SHA256 hashing prevents easy key guessing
- **No network exposure:** Authentication data never leaves the local machine

## Implementation Details

### C# Service (Worker.cs)

```csharp
private string GenerateAuthKey()
{
    var hostname = Dns.GetHostName();
    var username = Environment.UserName;
    var time = DateTime.UtcNow.ToString("HH00", CultureInfo.InvariantCulture);
    var data = String.Concat(hostname, username, time);

    using var sha256 = SHA256.Create();
    var bytes = Encoding.UTF8.GetBytes(data);
    var hash = sha256.ComputeHash(bytes);
    return Convert.ToBase64String(hash);
}
```

### Rust Client (run_command.rs)

```rust
fn generate_auth_key(&self) -> String {
    let hostname = whoami::hostname();
    let username = whoami::username();
    let time = chrono::Utc::now().format("%H00").to_string();
    let data = format!("{}{}{}", hostname, username, time);
    
    let mut hasher = Sha256::new();
    hasher.update(data.as_bytes());
    let result = hasher.finalize();
    
    base64::engine::general_purpose::STANDARD.encode(&result)
}
```

## Security Considerations

### Strengths
- **Local-only:** No network credentials to intercept
- **Time-limited:** Keys expire automatically every hour
- **User-specific:** Each user generates different keys
- **Machine-specific:** Each machine generates different keys

### Limitations
- **Not cryptographically secure:** This is basic protection, not enterprise-grade security
- **Clock dependency:** Requires synchronized system clocks
- **Predictable pattern:** Attackers who know the algorithm could generate keys
- **No replay protection:** Same request can be sent multiple times within the hour

### Recommendations
- Use only in trusted network environments
- Run service with minimal required privileges
- Monitor service logs for authentication failures
- Consider implementing additional security layers for production use

## Usage

### Testing Authentication
```bash
# Build and run the test program
cargo build --bin test_auth
cargo run --bin test_auth
```

### Expected Behavior
- ✅ Authenticated requests from correct user/machine succeed
- ❌ Requests without authentication keys are rejected
- ❌ Requests with incorrect authentication keys are rejected
- ❌ Requests from different users/machines are rejected (unless same hostname/username)

## Troubleshooting

### Authentication Failures
1. **Check system time:** Ensure client and service have synchronized clocks
2. **Verify username:** Make sure both client and service see the same username
3. **Check hostname:** Ensure both client and service resolve the same hostname
4. **Review logs:** Service logs authentication attempts with details

### Common Issues
- **Time zone differences:** Both client and service use UTC time
- **Username casing:** Username comparison is case-sensitive
- **Hostname resolution:** Different hostname resolution methods might cause mismatches

## Log Messages

### Successful Authentication
```
[INFO] Client connected
[INFO] Authentication successful
[INFO] Command 'whoami' executed with exit code 0
```

### Failed Authentication
```
[INFO] Client connected
[WARN] Authentication failed. Expected key length: 44, Client key length: 44
```

### Missing Authentication
```
[INFO] Client connected
[WARN] Request missing authentication key
```
