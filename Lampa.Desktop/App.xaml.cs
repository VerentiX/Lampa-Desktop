using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using Lampa.Desktop.Services;

namespace Lampa.Desktop;

public partial class App : System.Windows.Application
{
    private const string ShutdownEventName = @"Local\Lampa.Desktop.ShutdownForUninstall";
    private const string StoppedEventName = @"Local\Lampa.Desktop.StoppedForUninstall";
    private Mutex? _singleInstance;
    private EventWaitHandle? _shutdownEvent;
    private EventWaitHandle? _stoppedEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => arg.Equals("--shutdown-for-uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            SignalRunningInstanceAndWait();
            Shutdown();
            return;
        }

        if (!IsAdministrator())
        {
            RelaunchElevated(e.Args);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, "Lampa.Desktop.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _stoppedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StoppedEventName);
        var startInTray = e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        window.Show();
        if (startInTray)
        {
            window.Hide();
            window.ShowInTaskbar = false;
        }
        _ = Task.Run(() => WaitForUninstallSignal(window));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _stoppedEvent?.Set(); } catch { }
        _shutdownEvent?.Dispose();
        _stoppedEvent?.Dispose();
        _singleInstance?.Dispose();
        try { LogStore.Instance.Flush(); } catch { }
        base.OnExit(e);
    }

    private void WaitForUninstallSignal(MainWindow window)
    {
        try
        {
            _shutdownEvent?.WaitOne();
            Dispatcher.BeginInvoke(window.ExitApplicationForUninstall);
        }
        catch (ObjectDisposedException) { }
    }

    private static void SignalRunningInstanceAndWait()
    {
        try
        {
            using var stopped = EventWaitHandle.OpenExisting(StoppedEventName);
            using var shutdown = EventWaitHandle.OpenExisting(ShutdownEventName);
            shutdown.Set();
            stopped.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Lampa is not running.
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string[] args)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(QuoteArg))
            };
            if (string.IsNullOrWhiteSpace(start.FileName)) return;
            Process.Start(start);
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt.
        }
        catch
        {
        }
    }

    private static string QuoteArg(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.IndexOfAny([' ', '\t', '"']) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
