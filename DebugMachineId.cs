using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SelfcareService
{
    class DebugMachineId
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== C# Machine ID Debug ===");
            
            // Use the same logic as in SecureAuth.cs
            string machineId = GenerateMachineId();
            Console.WriteLine($"C# Machine ID: {machineId}");
            
            string hostname = Environment.MachineName;
            Console.WriteLine($"Hostname: {hostname}");
            
            string username = Environment.UserName;
            Console.WriteLine($"Username: {username}");
            
            // Show machine-id file content
            string machineIdPath = "/etc/machine-id";
            if (File.Exists(machineIdPath))
            {
                string systemMachineId = File.ReadAllText(machineIdPath).Trim();
                Console.WriteLine($"System machine-id: {systemMachineId}");
            }
            else
            {
                Console.WriteLine("System machine-id file not found");
            }
            
            Console.WriteLine($"Sample machine ID prefix: {machineId.Substring(0, Math.Min(16, machineId.Length))}");
        }
        
        private static string GenerateMachineId()
        {
            try
            {
                var components = new StringBuilder();
                
                // Add hostname
                string hostname = Environment.MachineName;
                components.Append(hostname);
                
                // Add username
                string username = Environment.UserName;
                components.Append(username);
                
                // Try to read system machine ID
                string machineIdPath = "/etc/machine-id";
                if (File.Exists(machineIdPath))
                {
                    string systemMachineId = File.ReadAllText(machineIdPath).Trim();
                    components.Append(systemMachineId);
                }
                else
                {
                    // Fallback for systems without machine-id
                    components.Append(Environment.OSVersion.ToString());
                }
                
                // Hash the combined components
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(components.ToString()));
                    return Convert.ToBase64String(hashBytes);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating machine ID: {ex.Message}");
                // Fallback to a simple hash of hostname + username
                string fallback = Environment.MachineName + Environment.UserName;
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(fallback));
                    return Convert.ToBase64String(hashBytes);
                }
            }
        }
    }
}
