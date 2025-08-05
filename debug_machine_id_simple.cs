using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

class Program
{
    static void Main()
    {
        Console.WriteLine("=== Machine ID Debug (Root Perspective) ===");
        
        string machineData = $"{Environment.MachineName}{Environment.UserName}";
        Console.WriteLine($"Hostname: {Environment.MachineName}");
        Console.WriteLine($"Username: {Environment.UserName}");
        
        // Add Linux machine-id
        try
        {
            if (File.Exists("/etc/machine-id"))
            {
                string systemMachineId = File.ReadAllText("/etc/machine-id").Trim();
                machineData += systemMachineId;
                Console.WriteLine($"System machine-id: {systemMachineId}");
            }
            else if (File.Exists("/var/lib/dbus/machine-id"))
            {
                string systemMachineId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                machineData += systemMachineId;
                Console.WriteLine($"D-Bus machine-id: {systemMachineId}");
            }
            else
            {
                machineData += Environment.ProcessorCount + Environment.OSVersion.ToString();
                Console.WriteLine("Using fallback: processor count + OS version");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading machine-id: {ex.Message}");
            machineData += Environment.ProcessorCount + Environment.OSVersion.ToString();
        }
        
        Console.WriteLine($"Combined machine data: {machineData}");
        
        // Hash the machine data to create a stable ID
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineData));
        string machineId = Convert.ToBase64String(hash);
        
        Console.WriteLine($"Final Machine ID: {machineId}");
        Console.WriteLine($"Machine ID (first 16 chars): {machineId.Substring(0, Math.Min(16, machineId.Length))}");
    }
}
