using System.Threading;
using System.Windows;

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

        _singleInstance = new Mutex(true, "Lampa.Desktop.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _stoppedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StoppedEventName);
        var window = new MainWindow();
        window.Show();
        _ = Task.Run(() => WaitForUninstallSignal(window));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _stoppedEvent?.Set(); } catch { }
        _shutdownEvent?.Dispose();
        _stoppedEvent?.Dispose();
        _singleInstance?.Dispose();
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
}
