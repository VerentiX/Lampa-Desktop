using Microsoft.Win32;

namespace Lampa.Desktop.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (enabled)
            key?.SetValue("Lampa", $"\"{Environment.ProcessPath}\" --background");
        else key?.DeleteValue("Lampa", false);
    }
}
