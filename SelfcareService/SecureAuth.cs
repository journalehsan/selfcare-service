using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfcareService;

public class SecureAuthData
{
    public string MachineId { get; set; } = string.Empty;
    public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public long CreatedAt { get; set; }
}

public class SecureAuthenticator
{
    // Hardcoded credentials matching Rust client
    private const string AUTH_USERNAME = "selfcare";
    private const string AUTH_PASSWORD = "SelfCare@#2025";
    private const string CACHE_FILE_NAME = "cache.db";

    private readonly string _cacheDir;
    private readonly string _machineId;

    public SecureAuthenticator()
    {
        _cacheDir = GetCacheDirectory();
        _machineId = GenerateMachineId();
        
        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDir);
    }

    private string GetCacheDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config");
        }
        else
        {
            // Linux
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config");
        }
    }

    private string GenerateMachineId()
    {
        var machineData = $"{Environment.MachineName}{Environment.UserName}";

        // Add platform-specific unique identifiers
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Try to get machine GUID on Windows
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "csproduct get UUID /value",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("UUID="))
                        {
                            var uuid = line.Substring(5).Trim();
                            if (!string.IsNullOrEmpty(uuid) && uuid != "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
                            {
                                machineData += uuid;
                            }
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Fallback: use processor count and OS version
                machineData += Environment.ProcessorCount + Environment.OSVersion.ToString();
            }
        }
        else
        {
            // Linux/macOS: try to get machine-id
            try
            {
                if (File.Exists("/etc/machine-id"))
                {
                    machineData += File.ReadAllText("/etc/machine-id").Trim();
                }
                else if (File.Exists("/var/lib/dbus/machine-id"))
                {
                    machineData += File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                }
            }
            catch
            {
                // Fallback
                machineData += Environment.ProcessorCount + Environment.OSVersion.ToString();
            }
        }

        // Hash the machine data to create a stable ID
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineData));
        return Convert.ToBase64String(hash);
    }

    private byte[] GetOrCreateEncryptionKey()
    {
        var cacheFile = Path.Combine(_cacheDir, CACHE_FILE_NAME);

        try
        {
            if (File.Exists(cacheFile))
            {
                var authData = LoadAuthData(cacheFile);
                if (authData != null && authData.MachineId == _machineId)
                {
                    return DecryptStoredKey(authData);
                }
            }
        }
        catch
        {
            // If loading fails, generate new key
        }

        // Generate new key and store it
        var key = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(key);
        
        StoreEncryptionKey(key, cacheFile);
        return key;
    }

    private void StoreEncryptionKey(byte[] key, string cacheFile)
    {
        // Generate a random nonce for encryption
        var nonce = new byte[12];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);

        // Use machine-specific data as encryption key for storage
        var storageKey = DeriveStorageKey();

        // Encrypt the actual key
        using var aes = new AesGcm(storageKey);
        var encryptedKey = new byte[key.Length];
        var tag = new byte[16];
        
        aes.Encrypt(nonce, key, encryptedKey, tag);

        var authData = new SecureAuthData
        {
            MachineId = _machineId,
            EncryptedKey = encryptedKey.Concat(tag).ToArray(),
            Nonce = nonce,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var json = JsonSerializer.Serialize(authData);
        File.WriteAllText(cacheFile, json);

        // Set restrictive permissions on Unix systems
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 {cacheFile}",
                    UseShellExecute = false
                });
                process?.WaitForExit();
            }
            catch
            {
                // Ignore chmod errors
            }
        }
    }

    private SecureAuthData? LoadAuthData(string cacheFile)
    {
        try
        {
            var json = File.ReadAllText(cacheFile);
            return JsonSerializer.Deserialize<SecureAuthData>(json);
        }
        catch
        {
            return null;
        }
    }

    private byte[] DecryptStoredKey(SecureAuthData authData)
    {
        var storageKey = DeriveStorageKey();
        
        using var aes = new AesGcm(storageKey);
        
        var encryptedKeyWithTag = authData.EncryptedKey;
        var encryptedKey = encryptedKeyWithTag[..^16];
        var tag = encryptedKeyWithTag[^16..];
        
        var decryptedKey = new byte[encryptedKey.Length];
        aes.Decrypt(authData.Nonce, encryptedKey, tag, decryptedKey);
        
        return decryptedKey;
    }

    private byte[] DeriveStorageKey()
    {
        var data = $"{_machineId}{AUTH_USERNAME}storage_key_derivation_salt";
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public string GenerateAuthToken()
    {
        var encryptionKey = GetOrCreateEncryptionKey();
        
        // Create authentication payload
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{AUTH_USERNAME}:{AUTH_PASSWORD}:{_machineId}:{timestamp}";

        // Encrypt the payload
        var nonce = new byte[12];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);

        using var aes = new AesGcm(encryptionKey);
        var encrypted = new byte[Encoding.UTF8.GetBytes(payload).Length];
        var tag = new byte[16];
        
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(payload), encrypted, tag);

        // Combine nonce, encrypted data, and tag
        var tokenData = nonce.Concat(encrypted).Concat(tag).ToArray();
        
        // Base64 encode for transmission
        return Convert.ToBase64String(tokenData);
    }

    public bool VerifyAuthToken(string token, int maxAgeSeconds = 300)
    {
        try
        {
            var encryptionKey = GetOrCreateEncryptionKey();

            // Decode base64
            var tokenData = Convert.FromBase64String(token);

            if (tokenData.Length < 28) // 12 (nonce) + 16 (tag) = minimum size
                return false;

            // Extract nonce, encrypted data, and tag
            var nonce = tokenData[..12];
            var encryptedWithTag = tokenData[12..];
            var encrypted = encryptedWithTag[..^16];
            var tag = encryptedWithTag[^16..];

            // Decrypt
            using var aes = new AesGcm(encryptionKey);
            var decrypted = new byte[encrypted.Length];
            aes.Decrypt(nonce, encrypted, tag, decrypted);

            var payload = Encoding.UTF8.GetString(decrypted);

            // Parse payload
            var parts = payload.Split(':');
            if (parts.Length != 4)
                return false;

            var username = parts[0];
            var password = parts[1];
            var machineId = parts[2];
            var timestampStr = parts[3];

            // Verify credentials
            if (username != AUTH_USERNAME || password != AUTH_PASSWORD)
                return false;

            // Verify machine ID
            if (machineId != _machineId)
                return false;

            // Verify timestamp (prevent replay attacks)
            if (!long.TryParse(timestampStr, out var timestamp))
                return false;

            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (currentTime - timestamp > maxAgeSeconds)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetMachineId() => _machineId;

    public void Initialize()
    {
        // Just ensure the encryption key is created
        GetOrCreateEncryptionKey();
        Console.WriteLine($"Secure authentication system initialized for machine: {_machineId[..16]}");
    }
}
