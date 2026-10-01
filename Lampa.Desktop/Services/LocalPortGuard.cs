using System.Net;
using System.Net.Sockets;

namespace Lampa.Desktop.Services;

/// <summary>
/// Windows Hyper-V/WinNAT reserves random TCP ranges after each reboot.
/// Binding 127.0.0.1:10809 then fails with WSAEACCES ("forbidden by its access permissions").
/// Probe the actual bind before starting sing-box and hop to a free port.
/// </summary>
internal static class LocalPortGuard
{
    public static int Pick(int preferred, params int[] used)
    {
        var blocked = used.ToHashSet();
        foreach (var port in Candidates(preferred))
        {
            if (blocked.Contains(port)) continue;
            if (CanBind(port)) return port;
        }
        throw new InvalidOperationException("Нет свободного локального порта для Lampa");
    }

    public static bool CanBind(int port)
    {
        if (port is <= 1024 or >= 65535) return false;
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen(1);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { socket?.Dispose(); } catch { }
        }
    }

    private static IEnumerable<int> Candidates(int preferred)
    {
        if (preferred is > 1024 and < 65535) yield return preferred;
        foreach (var port in new[] { 18080, 20809, 28080, 38080, 41809, 52809, 61809, 49512, 49664 })
            if (port != preferred) yield return port;
        for (var i = 1; i <= 40; i++)
        {
            var port = preferred + (i * 173);
            if (port is > 1024 and < 65000) yield return port;
        }
        for (var port = 49700; port <= 49800; port++)
            yield return port;
    }
}
