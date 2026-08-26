using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Lampa.Desktop.Services;

public static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public static void Enable(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true) ?? throw new InvalidOperationException("Нет доступа к системному прокси");
        key.SetValue("ProxyServer", $"127.0.0.1:{port}");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyOverride", "<local>");
        Refresh();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);
}
