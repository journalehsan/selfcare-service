using System.ComponentModel;
using System.ServiceProcess;

namespace SelfCareService
{
    [RunInstaller(true)]
    public partial class UptimeWatcherServiceInstaller : System.Configuration.Install.Installer
    {
        private ServiceInstaller serviceInstaller;
        private ServiceProcessInstaller processInstaller;

        public UptimeWatcherServiceInstaller()
        {
            // Service installer
            serviceInstaller = new ServiceInstaller();
            serviceInstaller.ServiceName = "SelfCareUptimeWatcher";
            serviceInstaller.DisplayName = "SelfCare Uptime Watcher";
            serviceInstaller.Description = "Monitors system uptime and shows reboot reminders to maintain system stability";
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            // Process installer
            processInstaller = new ServiceProcessInstaller();
            processInstaller.Account = ServiceAccount.LocalSystem;

            // Add installers
            Installers.Add(serviceInstaller);
            Installers.Add(processInstaller);
        }
    }

    partial class UptimeWatcherService
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            this.ServiceName = "SelfCareUptimeWatcher";
        }
    }
}
