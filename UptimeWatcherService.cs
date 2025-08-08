using System;
using System.IO;
using System.ServiceProcess;
using System.Timers;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
using System.Management;
using System.Threading.Tasks;

namespace SelfCareService
{
    public partial class UptimeWatcherService : ServiceBase
    {
        private Timer _monitorTimer;
        private const int UPTIME_THRESHOLD_HOURS = 12;
        private const int CHECK_INTERVAL_MINUTES = 30;
        private const string STATE_FILE_NAME = "uptime_skip_state.json";
        private readonly string _stateFilePath;
        private UptimeSkipState _state;

        public UptimeWatcherService()
        {
            InitializeComponent();
            
            // Set service properties
            ServiceName = "SelfCareUptimeWatcher";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;

            // Initialize state file path
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string serviceFolder = Path.Combine(programData, "SelfCare");
            Directory.CreateDirectory(serviceFolder);
            _stateFilePath = Path.Combine(serviceFolder, STATE_FILE_NAME);

            // Load or create initial state
            LoadState();
        }

        protected override void OnStart(string[] args)
        {
            WriteEventLog("SelfCare Uptime Watcher Service starting...", EventLogEntryType.Information);

            // Create and configure timer
            _monitorTimer = new Timer();
            _monitorTimer.Interval = CHECK_INTERVAL_MINUTES * 60 * 1000; // Convert to milliseconds
            _monitorTimer.Elapsed += OnTimerElapsed;
            _monitorTimer.AutoReset = true;
            _monitorTimer.Enabled = true;

            WriteEventLog("SelfCare Uptime Watcher Service started successfully", EventLogEntryType.Information);
        }

        protected override void OnStop()
        {
            WriteEventLog("SelfCare Uptime Watcher Service stopping...", EventLogEntryType.Information);

            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();

            WriteEventLog("SelfCare Uptime Watcher Service stopped", EventLogEntryType.Information);
        }

        private async void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                await CheckUptimeAndShowWarning();
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error in uptime check: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private async Task CheckUptimeAndShowWarning()
        {
            try
            {
                // Get system uptime
                TimeSpan uptime = GetSystemUptime();
                double uptimeHours = uptime.TotalHours;

                WriteEventLog($"Current system uptime: {uptime.Days} days, {uptime.Hours} hours, {uptime.Minutes} minutes", 
                    EventLogEntryType.Information);

                // Check if warning should be shown
                if (!ShouldShowWarning(uptimeHours))
                {
                    return;
                }

                // Show warning dialog in separate thread (required for Windows Service UI)
                await Task.Run(() => ShowRebootWarningDialog(uptime));
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error checking uptime: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private bool ShouldShowWarning(double uptimeHours)
        {
            // Check if uptime exceeds threshold
            if (uptimeHours < UPTIME_THRESHOLD_HOURS)
            {
                return false;
            }

            // Check if we're still in skip period
            if (_state.LastSkipTime.HasValue && _state.LastSkipDuration.HasValue)
            {
                DateTime skipEndTime = _state.LastSkipTime.Value.AddSeconds(_state.LastSkipDuration.Value.ToSeconds());
                if (DateTime.UtcNow < skipEndTime)
                {
                    return false; // Still in skip period
                }
            }

            return true;
        }

        private void ShowRebootWarningDialog(TimeSpan uptime)
        {
            try
            {
                // Create dialog on STA thread
                var dialogThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);

                        var dialog = new RebootWarningDialog(uptime, GetAvailableSkipOptions(), _state.AlertCount);
                        var result = dialog.ShowDialog();

                        if (result == DialogResult.Yes)
                        {
                            // User chose to reboot
                            WriteEventLog("User chose to reboot system", EventLogEntryType.Information);
                            InitiateReboot();
                        }
                        else if (dialog.SelectedSkipDuration.HasValue)
                        {
                            // User chose to skip
                            RecordSkip(dialog.SelectedSkipDuration.Value);
                            WriteEventLog($"User chose to skip for {dialog.SelectedSkipDuration.Value.ToDisplayString()}", 
                                EventLogEntryType.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteEventLog($"Error in dialog thread: {ex.Message}", EventLogEntryType.Error);
                    }
                });

                dialogThread.SetApartmentState(System.Threading.ApartmentState.STA);
                dialogThread.IsBackground = true;
                dialogThread.Start();
                dialogThread.Join(TimeSpan.FromMinutes(5)); // Timeout after 5 minutes
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error showing dialog: {ex.Message}", EventLogEntryType.Error);
                
                // Fallback: Use VBScript for dialog
                ShowVBScriptDialog(uptime);
            }
        }

        private void ShowVBScriptDialog(TimeSpan uptime)
        {
            try
            {
                string message = $"System Uptime Warning\\n\\n" +
                               $"Your system has been running for {uptime.Days} days and {uptime.Hours} hours.\\n\\n" +
                               $"Regular reboots help maintain system stability and apply important updates.\\n\\n" +
                               $"Would you like to reboot now?";

                string vbScript = $@"
Set objShell = CreateObject(""WScript.Shell"")
intResult = objShell.Popup(""{message}"", 300, ""SelfCare - Reboot Reminder"", 4 + 48)
If intResult = 6 Then
    WScript.Echo ""REBOOT""
Else
    WScript.Echo ""SKIP""
End If
";

                string scriptPath = Path.Combine(Path.GetTempPath(), "reboot_warning.vbs");
                File.WriteAllText(scriptPath, vbScript);

                var process = new Process();
                process.StartInfo.FileName = "cscript";
                process.StartInfo.Arguments = $"//NoLogo \"{scriptPath}\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                File.Delete(scriptPath);

                if (output.Trim() == "REBOOT")
                {
                    WriteEventLog("User chose to reboot (VBScript)", EventLogEntryType.Information);
                    InitiateReboot();
                }
                else
                {
                    RecordSkip(SkipDuration.Minutes10); // Default skip
                    WriteEventLog("User chose to skip (VBScript)", EventLogEntryType.Information);
                }
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error in VBScript dialog: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private SkipDuration[] GetAvailableSkipOptions()
        {
            return _state.AlertCount switch
            {
                0 => new[] { SkipDuration.Hours12, SkipDuration.Hours10, SkipDuration.Hours3, SkipDuration.Minutes10 },
                1 => new[] { SkipDuration.Hours10, SkipDuration.Hours3, SkipDuration.Minutes10 },
                2 => new[] { SkipDuration.Hours3, SkipDuration.Minutes10 },
                _ => new[] { SkipDuration.Minutes10 }
            };
        }

        private void RecordSkip(SkipDuration duration)
        {
            try
            {
                _state.AlertCount++;
                _state.LastSkipTime = DateTime.UtcNow;
                _state.LastSkipDuration = duration;
                SaveState();
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error recording skip: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private void InitiateReboot()
        {
            try
            {
                // Reset state after reboot decision
                _state = new UptimeSkipState();
                SaveState();

                // Initiate system reboot
                var process = new Process();
                process.StartInfo.FileName = "shutdown";
                process.StartInfo.Arguments = "/r /t 60 /c \"System reboot initiated by SelfCare\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error initiating reboot: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private TimeSpan GetSystemUptime()
        {
            try
            {
                // Method 1: Using WMI
                using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        string lastBootTime = obj["LastBootUpTime"].ToString();
                        DateTime bootTime = ManagementDateTimeConverter.ToDateTime(lastBootTime);
                        return DateTime.Now - bootTime;
                    }
                }
            }
            catch
            {
                // Fallback: Use Performance Counter
                try
                {
                    using (var uptime = new PerformanceCounter("System", "System Up Time"))
                    {
                        uptime.NextValue(); // First call returns 0
                        return TimeSpan.FromSeconds(uptime.NextValue());
                    }
                }
                catch
                {
                    // Last resort: Use registry
                    return GetUptimeFromRegistry();
                }
            }

            return TimeSpan.Zero;
        }

        private TimeSpan GetUptimeFromRegistry()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Windows"))
                {
                    if (key?.GetValue("ShutdownTime") is byte[] shutdownBytes && shutdownBytes.Length == 8)
                    {
                        long shutdownTime = BitConverter.ToInt64(shutdownBytes, 0);
                        DateTime shutdown = DateTime.FromFileTime(shutdownTime);
                        return DateTime.Now - shutdown;
                    }
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        private void LoadState()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    string json = File.ReadAllText(_stateFilePath);
                    _state = JsonSerializer.Deserialize<UptimeSkipState>(json) ?? new UptimeSkipState();
                }
                else
                {
                    _state = new UptimeSkipState();
                }
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error loading state: {ex.Message}", EventLogEntryType.Warning);
                _state = new UptimeSkipState();
            }
        }

        private void SaveState()
        {
            try
            {
                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                WriteEventLog($"Error saving state: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private void WriteEventLog(string message, EventLogEntryType type)
        {
            try
            {
                if (!EventLog.SourceExists(ServiceName))
                {
                    EventLog.CreateEventSource(ServiceName, "Application");
                }
                EventLog.WriteEntry(ServiceName, message, type);
            }
            catch
            {
                // If event log fails, write to file
                try
                {
                    string logPath = Path.Combine(Path.GetDirectoryName(_stateFilePath), "service.log");
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{type}] {message}{Environment.NewLine}";
                    File.AppendAllText(logPath, logEntry);
                }
                catch { }
            }
        }
    }

    // Data structures
    public class UptimeSkipState
    {
        public int AlertCount { get; set; } = 0;
        public DateTime? LastSkipTime { get; set; }
        public SkipDuration? LastSkipDuration { get; set; }
    }

    public enum SkipDuration
    {
        Hours12,
        Hours10,
        Hours3,
        Minutes10
    }

    public static class SkipDurationExtensions
    {
        public static int ToSeconds(this SkipDuration duration)
        {
            return duration switch
            {
                SkipDuration.Hours12 => 12 * 3600,
                SkipDuration.Hours10 => 10 * 3600,
                SkipDuration.Hours3 => 3 * 3600,
                SkipDuration.Minutes10 => 10 * 60,
                _ => 10 * 60
            };
        }

        public static string ToDisplayString(this SkipDuration duration)
        {
            return duration switch
            {
                SkipDuration.Hours12 => "12 hours",
                SkipDuration.Hours10 => "10 hours", 
                SkipDuration.Hours3 => "3 hours",
                SkipDuration.Minutes10 => "10 minutes",
                _ => "10 minutes"
            };
        }
    }
}
