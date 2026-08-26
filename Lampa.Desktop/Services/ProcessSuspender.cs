using System.Runtime.InteropServices;

namespace Lampa.Desktop.Services;

internal static class ProcessSuspender
{
    public static bool Suspend(int processId) => Toggle(processId, suspend: true);
    public static bool Resume(int processId) => Toggle(processId, suspend: false);

    private static bool Toggle(int processId, bool suspend)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var status = suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle);
            return status >= 0;
        }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    private const int ProcessSuspendResume = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
