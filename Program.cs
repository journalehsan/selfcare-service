using System;
using System.ServiceProcess;

namespace SelfCareService
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "install":
                        InstallService();
                        break;
                    case "uninstall":
                        UninstallService();
                        break;
                    case "test":
                        // Run as console application for testing
                        TestService();
                        break;
                    default:
                        ShowUsage();
                        break;
                }
            }
            else
            {
                // Run as Windows Service
                ServiceBase[] ServicesToRun;
                ServicesToRun = new ServiceBase[]
                {
                    new UptimeWatcherService()
                };
                ServiceBase.Run(ServicesToRun);
            }
        }

        private static void InstallService()
        {
            try
            {
                var installer = new UptimeWatcherServiceInstaller();
                Console.WriteLine("Installing SelfCare Uptime Watcher Service...");
                // Installation would be handled by InstallUtil.exe or sc.exe
                Console.WriteLine("Use 'sc create SelfCareUptimeWatcher binPath=\"{0}\" start=auto'", 
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                Console.WriteLine("Then use 'sc start SelfCareUptimeWatcher' to start the service");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Installation failed: {ex.Message}");
            }
        }

        private static void UninstallService()
        {
            try
            {
                Console.WriteLine("Uninstalling SelfCare Uptime Watcher Service...");
                Console.WriteLine("Use 'sc stop SelfCareUptimeWatcher' then 'sc delete SelfCareUptimeWatcher'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Uninstallation failed: {ex.Message}");
            }
        }

        private static void TestService()
        {
            Console.WriteLine("Running SelfCare Uptime Watcher in test mode...");
            Console.WriteLine("Press Ctrl+C to stop");

            var service = new UptimeWatcherService();
            
            // Simulate service start
            var startMethod = typeof(UptimeWatcherService).GetMethod("OnStart", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            startMethod?.Invoke(service, new object[] { new string[0] });

            // Keep running until user stops
            Console.CancelKeyPress += (s, e) => {
                Console.WriteLine("\nStopping service...");
                var stopMethod = typeof(UptimeWatcherService).GetMethod("OnStop", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                stopMethod?.Invoke(service, null);
                e.Cancel = true;
                Environment.Exit(0);
            };

            // Keep the console app running
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
            }
        }

        private static void ShowUsage()
        {
            Console.WriteLine("SelfCare Uptime Watcher Service");
            Console.WriteLine("Usage:");
            Console.WriteLine("  SelfCareService.exe          - Run as Windows Service");
            Console.WriteLine("  SelfCareService.exe install  - Show installation commands");
            Console.WriteLine("  SelfCareService.exe uninstall- Show uninstallation commands");
            Console.WriteLine("  SelfCareService.exe test     - Run in console mode for testing");
        }
    }
}
