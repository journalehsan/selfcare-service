using System.Diagnostics;
using System.Text.Json;

#if WINDOWS
using System.Management;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
#endif

namespace SelfcareService;

public class UptimeMonitor
{
    private readonly ILogger _logger;
    private UptimeSkipState _state;
    private readonly string _stateFilePath;

    private const int UPTIME_THRESHOLD_HOURS = 12;

    public UptimeMonitor(ILogger logger)
    {
        _logger = logger;
        _stateFilePath = GetStateFilePath();
        LoadState();
    }

    public async Task CheckUptimeAndShowWarning()
    {
        try
        {
            var uptime = GetSystemUptime();
            _logger.LogInformation($"Current system uptime: {uptime.Days} days, {uptime.Hours} hours, {uptime.Minutes} minutes");

            if (!ShouldShowWarning())
            {
                return;
            }

            var userChoice = await ShowRebootWarningDialog();
            _logger.LogInformation($"User chose: {userChoice}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in uptime check");
        }
    }

    public bool ShouldShowWarning()
    {
        var uptime = GetSystemUptime();
        
        // Check if uptime exceeds threshold
        if (uptime.TotalHours < UPTIME_THRESHOLD_HOURS)
        {
            return false;
        }

        // Check if we're still in skip period
        if (_state.LastSkipTime.HasValue && _state.LastSkipDuration.HasValue)
        {
            var skipEndTime = _state.LastSkipTime.Value.AddSeconds(_state.LastSkipDuration.Value.ToSeconds());
            if (DateTime.UtcNow < skipEndTime)
            {
                return false; // Still in skip period
            }
        }

        return true;
    }

    public async Task<string> ShowRebootWarningDialog()
    {
        try
        {
            var uptime = GetSystemUptime();
            var availableOptions = GetAvailableSkipOptions();

            if (OperatingSystem.IsWindows())
            {
                return await ShowWindowsDialog(uptime, availableOptions);
            }
            else
            {
                return await ShowLinuxDialog(uptime, availableOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing reboot dialog");
            return "error";
        }
    }

    private async Task<string> ShowWindowsDialog(TimeSpan uptime, SkipDuration[] availableOptions)
    {
        try
        {
            // Use VBScript for better service compatibility
            return await ShowVBScriptDialog(uptime, availableOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing Windows dialog");
            
            // Fallback: Record default skip
            RecordSkip(SkipDuration.Minutes10);
            return "skip_10min";
        }
    }

    private async Task<string> ShowVBScriptDialog(TimeSpan uptime, SkipDuration[] availableOptions)
    {
        try
        {
            string message = uptime.Days > 0 
                ? $"System Uptime Warning\\n\\nYour system has been running for {uptime.Days} days and {uptime.Hours} hours.\\n\\nRegular reboots help maintain system stability and apply important updates.\\n\\nThis is alert #{_state.AlertCount + 1}. Available skip options are being reduced.\\n\\nWould you like to reboot now?"
                : $"System Uptime Warning\\n\\nYour system has been running for {uptime.Hours} hours and {uptime.Minutes} minutes.\\n\\nRegular reboots help maintain system stability and apply important updates.\\n\\nThis is alert #{_state.AlertCount + 1}. Available skip options are being reduced.\\n\\nWould you like to reboot now?";

            // Create VBScript with multiple buttons
            var skipButtons = string.Join("", availableOptions.Select((option, index) => 
                $"If intResult = {index + 7} Then\\n    WScript.Echo \"skip_{option.ToString().ToLower()}\"\\nEnd If\\n"));

            string vbScript = $@"
Set objShell = CreateObject(""WScript.Shell"")
intResult = objShell.Popup(""{message}"", 300, ""SelfCare - Reboot Reminder"", 4 + 48 + 256)
If intResult = 6 Then
    WScript.Echo ""reboot""
ElseIf intResult = 7 Then
    WScript.Echo ""skip_minutes10""
Else
    WScript.Echo ""skip_minutes10""
End If
";

            string scriptPath = Path.Combine(Path.GetTempPath(), "reboot_warning.vbs");
            await File.WriteAllTextAsync(scriptPath, vbScript);

            var process = new Process();
            process.StartInfo.FileName = "cscript";
            process.StartInfo.Arguments = $"//NoLogo \"{scriptPath}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            File.Delete(scriptPath);

            var result = output.Trim().ToLower();
            if (result == "reboot")
            {
                await InitiateReboot();
                return "reboot";
            }
            else
            {
                // Parse skip duration and record it
                var skipDuration = ParseSkipDuration(result);
                RecordSkip(skipDuration);
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VBScript dialog");
            RecordSkip(SkipDuration.Minutes10);
            return "skip_minutes10";
        }
    }

    private async Task<string> ShowLinuxDialog(TimeSpan uptime, SkipDuration[] availableOptions)
    {
        try
        {
            string message = uptime.Days > 0 
                ? $"System has been running for {uptime.Days} days and {uptime.Hours} hours. Regular reboots help maintain stability. This is alert #{_state.AlertCount + 1}."
                : $"System has been running for {uptime.Hours} hours and {uptime.Minutes} minutes. Regular reboots help maintain stability. This is alert #{_state.AlertCount + 1}.";

            // Try different dialog methods
            var result = await TryZenityDialog(message, availableOptions) 
                      ?? await TryKDialogDialog(message, availableOptions)
                      ?? await TryWhiptailDialog(message, availableOptions)
                      ?? "skip_minutes10";

            if (result == "reboot")
            {
                await InitiateLinuxReboot();
                return "reboot";
            }
            else
            {
                var skipDuration = ParseSkipDuration(result);
                RecordSkip(skipDuration);
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing Linux dialog");
            RecordSkip(SkipDuration.Minutes10);
            return "skip_minutes10";
        }
    }

    private async Task<string?> TryZenityDialog(string message, SkipDuration[] availableOptions)
    {
        try
        {
            var buttons = new List<string> { "Reboot Now" };
            buttons.AddRange(availableOptions.Select(opt => $"Skip {opt.ToDisplayString()}"));

            var buttonArgs = buttons.SelectMany(btn => new[] { "--extra-button", btn }).ToList();
            var args = new List<string>
            {
                "--question",
                "--title=SelfCare - Reboot Reminder",
                $"--text={message}",
                "--ok-label=Cancel"
            };
            args.AddRange(buttonArgs);

            var process = new Process();
            process.StartInfo.FileName = "zenity";
            process.StartInfo.Arguments = string.Join(" ", args.Select(arg => $"\"{arg}\""));
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var result = output.Trim();
            if (result == "Reboot Now") return "reboot";
            if (result.Contains("12 hours")) return "skip_hours12";
            if (result.Contains("10 hours")) return "skip_hours10";
            if (result.Contains("3 hours")) return "skip_hours3";
            if (result.Contains("10 minutes")) return "skip_minutes10";
            
            return "skip_minutes10";
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryKDialogDialog(string message, SkipDuration[] availableOptions)
    {
        try
        {
            var menuItems = new List<string> { "1", "Reboot Now" };
            int itemId = 2;
            
            foreach (var option in availableOptions)
            {
                menuItems.Add(itemId.ToString());
                menuItems.Add($"Skip {option.ToDisplayString()}");
                itemId++;
            }

            var process = new Process();
            process.StartInfo.FileName = "kdialog";
            process.StartInfo.Arguments = $"--menu \"{message}\" --title \"SelfCare - Reboot Reminder\" {string.Join(" ", menuItems.Select(item => $"\"{item}\""))}";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (int.TryParse(output.Trim(), out int choice))
            {
                if (choice == 1) return "reboot";
                if (choice >= 2 && choice - 2 < availableOptions.Length)
                {
                    var selectedOption = availableOptions[choice - 2];
                    return $"skip_{selectedOption.ToString().ToLower()}";
                }
            }
            
            return "skip_minutes10";
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryWhiptailDialog(string message, SkipDuration[] availableOptions)
    {
        try
        {
            var menuItems = new List<string> { "1", "Reboot Now" };
            int itemId = 2;
            
            foreach (var option in availableOptions)
            {
                menuItems.Add(itemId.ToString());
                menuItems.Add($"Skip {option.ToDisplayString()}");
                itemId++;
            }

            var process = new Process();
            process.StartInfo.FileName = "whiptail";
            process.StartInfo.Arguments = $"--menu \"{message}\" 20 70 10 {string.Join(" ", menuItems.Select(item => $"\"{item}\""))}";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (int.TryParse(output.Trim(), out int choice))
            {
                if (choice == 1) return "reboot";
                if (choice >= 2 && choice - 2 < availableOptions.Length)
                {
                    var selectedOption = availableOptions[choice - 2];
                    return $"skip_{selectedOption.ToString().ToLower()}";
                }
            }
            
            return "skip_minutes10";
        }
        catch
        {
            return null;
        }
    }

    private SkipDuration ParseSkipDuration(string result)
    {
        return result.ToLower() switch
        {
            "skip_hours12" => SkipDuration.Hours12,
            "skip_hours10" => SkipDuration.Hours10,
            "skip_hours3" => SkipDuration.Hours3,
            "skip_minutes10" => SkipDuration.Minutes10,
            _ => SkipDuration.Minutes10
        };
    }

    private async Task InitiateReboot()
    {
        try
        {
            // Reset state after reboot decision
            _state = new UptimeSkipState();
            SaveState();

            var process = new Process();
            process.StartInfo.FileName = "shutdown";
            process.StartInfo.Arguments = "/r /t 60 /c \"System reboot initiated by SelfCare\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            
            _logger.LogInformation("System reboot initiated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating reboot");
        }
    }

    private async Task InitiateLinuxReboot()
    {
        try
        {
            // Reset state after reboot decision
            _state = new UptimeSkipState();
            SaveState();

            var process = new Process();
            process.StartInfo.FileName = "systemctl";
            process.StartInfo.Arguments = "reboot";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            
            _logger.LogInformation("System reboot initiated (Linux)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating Linux reboot");
        }
    }

    public TimeSpan GetSystemUptime()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsUptime();
        }
        else
        {
            return GetLinuxUptime();
        }
    }

    private TimeSpan GetWindowsUptime()
    {
#if WINDOWS
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
                _logger.LogWarning("Failed to get uptime from WMI and Performance Counter");
                return TimeSpan.Zero;
            }
        }

        return TimeSpan.Zero;
#else
        // On non-Windows, use alternative method
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "uptime";
            process.StartInfo.Arguments = "-s";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (DateTime.TryParse(output.Trim(), out DateTime bootTime))
            {
                return DateTime.Now - bootTime;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Windows uptime on non-Windows platform");
        }
        return TimeSpan.Zero;
#endif
    }

    private TimeSpan GetLinuxUptime()
    {
        try
        {
            var uptimeText = File.ReadAllText("/proc/uptime");
            var uptimeSeconds = double.Parse(uptimeText.Split(' ')[0]);
            return TimeSpan.FromSeconds(uptimeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Linux uptime");
            return TimeSpan.Zero;
        }
    }

    public string GetFormattedUptime()
    {
        var uptime = GetSystemUptime();
        if (uptime.Days > 0)
        {
            return $"{uptime.Days} days, {uptime.Hours} hours, {uptime.Minutes} minutes";
        }
        else if (uptime.Hours > 0)
        {
            return $"{uptime.Hours} hours, {uptime.Minutes} minutes";
        }
        else
        {
            return $"{uptime.Minutes} minutes";
        }
    }

    public object GetUptimeStatus()
    {
        var uptime = GetSystemUptime();
        return new
        {
            CurrentUptime = GetFormattedUptime(),
            TotalHours = uptime.TotalHours,
            ShouldShowWarning = ShouldShowWarning(),
            AlertCount = _state.AlertCount,
            LastSkipTime = _state.LastSkipTime,
            LastSkipDuration = _state.LastSkipDuration?.ToDisplayString(),
            AvailableSkipOptions = GetAvailableSkipOptions().Select(opt => opt.ToDisplayString()).ToArray()
        };
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
            
            _logger.LogInformation($"Recorded skip: {duration.ToDisplayString()}, Alert count: {_state.AlertCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording skip");
        }
    }

    private string GetStateFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string serviceFolder = Path.Combine(programData, "SelfCare");
            Directory.CreateDirectory(serviceFolder);
            return Path.Combine(serviceFolder, "uptime_skip_state.json");
        }
        else
        {
            string systemDir = "/var/lib/selfcare";
            if (Directory.Exists(systemDir) || Directory.CreateDirectory(systemDir).Exists)
            {
                return Path.Combine(systemDir, "uptime_skip_state.json");
            }
            else
            {
                string userHome = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
                string configDir = Path.Combine(userHome, ".config", "selfcare");
                Directory.CreateDirectory(configDir);
                return Path.Combine(configDir, "uptime_skip_state.json");
            }
        }
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
            _logger.LogWarning(ex, "Error loading uptime state, using default");
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
            _logger.LogError(ex, "Error saving uptime state");
        }
    }
}

// Data structures for uptime monitoring
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
