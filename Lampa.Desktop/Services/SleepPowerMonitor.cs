using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Lampa.Desktop.Services;

/// <summary>
/// Pause the VPN only when the machine is actually leaving the running state:
/// classic S3/S4, Modern Standby (S0ix via suspend/resume notification),
/// or lid-close without an external display.
/// Display-off, Win+L and session idle (watching a video without mouse) are
/// not sleep — idle used to kill the core after a few minutes of YouTube.
/// </summary>
public sealed class SleepPowerMonitor : IDisposable
{
    public event Action<bool>? SleepRequested;
    public event Action? WakeRequested;

    // Display is tracked only to distinguish clamshell (lid closed + panel on).
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    // winnt.h GUID_LIDSWITCH_STATE_CHANGE
    private static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private readonly DeviceNotifyCallbackRoutine _suspendResumeCallback;
    private readonly List<IntPtr> _settingRegistrations = [];
    private readonly object _gate = new();
    private HwndSource? _source;
    private IntPtr _suspendResumeHandle;
    private System.Threading.Timer? _debounce;
    private bool? _displayOn;
    private bool? _lidOpen;
    private bool _classicSuspend;
    private bool _announcedSleep;
    private bool _disposed;

    public SleepPowerMonitor()
    {
        _suspendResumeCallback = OnSuspendResumeCallback;
        var parameters = new HwndSourceParameters("Lampa.SleepMonitor")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        RegisterPowerSettings(_source.Handle);
        RegisterSuspendResume();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void RegisterPowerSettings(IntPtr hwnd)
    {
        foreach (var guid in new[] { ConsoleDisplayState, SessionDisplayStatus, LidSwitchStateChange })
        {
            var copy = guid;
            var handle = RegisterPowerSettingNotification(hwnd, ref copy, DeviceNotifyWindowHandle);
            if (handle != IntPtr.Zero) _settingRegistrations.Add(handle);
        }
    }

    private void RegisterSuspendResume()
    {
        var recipient = new DeviceNotifySubscribeParameters
        {
            Callback = _suspendResumeCallback,
            Context = IntPtr.Zero
        };
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref recipient, out var handle) == 0)
            _suspendResumeHandle = handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast)
            HandlePowerBroadcast(unchecked((int)wParam.ToInt64()), lParam);
        return IntPtr.Zero;
    }

    private void HandlePowerBroadcast(int eventType, IntPtr lParam)
    {
        if (eventType is PbtApmSuspend)
        {
            SetClassicSuspend(true);
            return;
        }

        if (eventType is PbtApmResumeSuspend or PbtApmResumeAutomatic)
        {
            SetClassicSuspend(false);
            return;
        }

        if (eventType != PbtPowerSettingChange || lParam == IntPtr.Zero) return;
        var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
        if (setting.DataLength < 4) return;

        if (setting.PowerSetting == ConsoleDisplayState || setting.PowerSetting == SessionDisplayStatus)
        {
            // 0 off, 1 on, 2 dim — display-off is not sleep; keep it for lid/clamshell only.
            if (setting.Data == 2) return;
            lock (_gate) _displayOn = setting.Data != 0;
            ScheduleEvaluate();
            return;
        }

        if (setting.PowerSetting == LidSwitchStateChange)
        {
            lock (_gate) _lidOpen = setting.Data != 0;
            ScheduleEvaluate();
        }
    }

    private uint OnSuspendResumeCallback(IntPtr context, uint type, IntPtr setting)
    {
        // This is the Modern Standby path: WM_POWERBROADCAST often never
        // arrives for S0ix, but powrprof still delivers PBT_APMSUSPEND.
        if (type == PbtApmSuspend) SetClassicSuspend(true);
        else if (type is PbtApmResumeSuspend or PbtApmResumeAutomatic) SetClassicSuspend(false);
        return 0;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SetClassicSuspend(true);
        else if (e.Mode == PowerModes.Resume) SetClassicSuspend(false);
    }

    private void SetClassicSuspend(bool suspended)
    {
        lock (_gate) _classicSuspend = suspended;
        ScheduleEvaluate(immediate: true);
    }

    private bool ShouldPause()
    {
        lock (_gate)
        {
            if (_classicSuspend) return true;
            // Closed lid with the panel still on is clamshell + external monitor.
            if (_lidOpen == false && _displayOn != true) return true;
            return false;
        }
    }

    private void ScheduleEvaluate(bool immediate = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            var delay = immediate ? 0 : ShouldPause() ? 1500 : 400;
            _debounce = new System.Threading.Timer(_ => Evaluate(), null, delay, Timeout.Infinite);
        }
    }

    private void Evaluate()
    {
        bool pause;
        bool changed;
        bool isClassic;
        lock (_gate)
        {
            if (_disposed) return;
            pause = ShouldPause();
            changed = pause != _announcedSleep;
            isClassic = _classicSuspend;
            if (changed) _announcedSleep = pause;
        }

        if (!changed) return;
        if (pause) SleepRequested?.Invoke(isClassic);
        else WakeRequested?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        foreach (var handle in _settingRegistrations)
        {
            try { UnregisterPowerSettingNotification(handle); } catch { }
        }
        _settingRegistrations.Clear();
        if (_suspendResumeHandle != IntPtr.Zero)
        {
            try { PowerUnregisterSuspendResumeNotification(_suspendResumeHandle); } catch { }
            _suspendResumeHandle = IntPtr.Zero;
        }
        _source?.Dispose();
        _source = null;
    }

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0;
    private const int DeviceNotifyCallback = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
