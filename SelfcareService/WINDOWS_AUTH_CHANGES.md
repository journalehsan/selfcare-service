# C# Service Windows Authentication Changes

## Problem Solved
The selfcare service and Rust app had authentication mismatches on Windows because:
- **Service**: Runs as LocalSystem (SYSTEM account)  
- **App**: Runs as the current user account

This created different authentication contexts, causing communication failures.

## Solution Implemented
Modified the `GenerateAuthKey()` method in `Worker.cs` to use platform-specific authentication:

### Windows Authentication
- **Auth Data**: `hostname + time` only
- **Rationale**: Eliminates username dependency to avoid LocalSystem vs user mismatch
- **Format**: `{hostname}{time_utc}`

### Linux/Unix Authentication  
- **Auth Data**: `hostname + username + time` (unchanged)
- **Rationale**: Maintains backward compatibility with existing systemd service
- **Format**: `{hostname}{username}{time_utc}`

## Code Changes

### Before (lines 115-126)
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

### After (updated implementation)
```csharp
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
```

## Synchronization with Rust App
This change matches the authentication logic implemented in the Rust app (`src/core/run_command.rs`):

```rust
// Rust app authentication logic (for reference)
let data = if cfg!(windows) {
    let auth_data = format!("{}{}", hostname, time_utc);
    auth_data
} else {
    let username = "root".to_string();
    let auth_data = format!("{}{}{}", hostname, username, time_utc);
    auth_data
};
```

## Benefits
1. **Resolves Windows Authentication Issues**: Service and app now use consistent auth method
2. **Maintains Linux Compatibility**: Existing systemd service authentication unchanged
3. **Platform-Appropriate**: Each OS uses authentication suitable for its service model
4. **Debug Support**: Added comprehensive logging for troubleshooting

## Testing
1. ✅ Test Windows LocalSystem service with user app
2. ✅ Test Linux systemd service with root privileges  
3. ✅ Verify authentication works across different user accounts
4. ✅ Confirm no regression in existing functionality

## Deployment Notes
- Deploy this service update alongside the Rust app changes
- Both components must use the same authentication logic
- Test in development environment before production deployment
