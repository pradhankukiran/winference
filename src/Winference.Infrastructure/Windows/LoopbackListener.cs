using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Winference.Infrastructure.Windows;

public static class LoopbackListener
{
    public static int FindCandidatePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public static bool IsOwned(int port, int expectedPid)
    {
        uint size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0); // IPv4 OWNER_PID_LISTENER
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var error = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (error != 0) throw new Win32Exception((int)error);
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                var localAddress = unchecked((uint)Marshal.ReadInt32(row, 4));
                if (localPort == port && Marshal.ReadInt32(row, 20) == expectedPid && localAddress == 0x0100007f) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);
}
