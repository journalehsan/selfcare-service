using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

public class Program
{
    public static void Main()
    {
        Console.WriteLine("C# Authentication Debug");
        Console.WriteLine("======================");
        
        // Get the same values as the service
        var hostname = Dns.GetHostName();
        var username = Environment.UserName;
        var time = DateTime.UtcNow.ToString("HH00", CultureInfo.InvariantCulture);
        var data = String.Concat(hostname, username, time);
        
        Console.WriteLine($"Hostname: '{hostname}'");
        Console.WriteLine($"Username: '{username}'");
        Console.WriteLine($"Time (UTC): '{time}'");
        Console.WriteLine($"Data string: '{data}'");
        
        // Generate auth key exactly like the service
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(data);
        var hash = sha256.ComputeHash(bytes);
        var authKey = Convert.ToBase64String(hash);
        
        Console.WriteLine($"Auth key: '{authKey}'");
        Console.WriteLine($"Auth key length: {authKey.Length}");
        
        // Also show some debugging info
        Console.WriteLine();
        Console.WriteLine("Additional Debug Info:");
        Console.WriteLine($"Current UTC time: {DateTime.UtcNow}");
        Console.WriteLine($"Current local time: {DateTime.Now}");
        Console.WriteLine($"Environment.MachineName: '{Environment.MachineName}'");
        Console.WriteLine($"Environment.UserDomainName: '{Environment.UserDomainName}'");
    }
}
