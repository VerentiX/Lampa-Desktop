using System.Diagnostics;
using Microsoft.Win32;

namespace Lampa.Desktop.Services;

/// <summary>
/// HKCU Run cannot elevate. A logon task with HIGHEST starts Lampa already
/// elevated so the UAC prompt does not appear after every reboot, and the
/// requireAdministrator manifest is satisfied.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lampa";
    public const string TaskName = "Lampa Desktop";

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    private static void Enable()
    {
        var exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(exe)) return;
        var created = CreateLogonTask(exe);
        if (created) DeleteRunKey();
        else SetRunKey($"\"{exe}\" --background");
    }

    private static void Disable()
    {
        DeleteLogonTask();
        DeleteRunKey();
    }

    private static bool CreateLogonTask(string exe)
    {
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --background\" /SC ONLOGON /RL HIGHEST /F /IT";
        return RunSchtasks(args, ignoreExit: false);
    }

    private static void DeleteLogonTask() =>
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F", ignoreExit: true);

    private static bool RunSchtasks(string arguments, bool ignoreExit)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (process is null) return false;
            process.WaitForExit(8000);
            return ignoreExit || process.ExitCode == 0;
        }
        catch
        {
            return ignoreExit;
        }
    }

    private static void SetRunKey(string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.SetValue(RunValueName, command);
    }

    private static void DeleteRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(RunValueName, false);
    }
}
