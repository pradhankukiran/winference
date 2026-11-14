using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Winference.Infrastructure.Developer;

/// <summary>The program on this PC that sent a request.</summary>
/// <param name="Key">Stable identity for permissions: the program's full path, lower case.</param>
public sealed record CallerApp(string Key, string Name, string? Path, int? ProcessId)
{
    public static CallerApp Unknown { get; } = new("unknown", "An unknown app", null, null);
}

/// <summary>
/// Finds which program opened a loopback connection. Windows lists every TCP connection with
/// the process that owns it, and the app's end is the row whose ports mirror the server's.
/// </summary>
public static class CallerLookup
{
    public static CallerApp Identify(IPEndPoint client, int serverPort)
    {
        try { return FindProcessId(client, serverPort) is { } id ? Describe(id) : CallerApp.Unknown; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or ExternalException)
        {
            return CallerApp.Unknown;
        }
    }

    public static int? FindProcessId(IPEndPoint client, int serverPort)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var v6 = client.AddressFamily == AddressFamily.InterNetworkV6;
        var table = ReadTable(v6 ? AfInet6 : AfInet);
        if (table is null) return null;
        try
        {
            var count = Marshal.ReadInt32(table.Value);
            var rowSize = v6 ? 56 : 24;
            for (var i = 0; i < count; i++)
            {
                var row = table.Value + 4 + i * rowSize;
                int localPort, remotePort, pid;
                if (v6)
                {
                    localPort = Port(Marshal.ReadInt32(row, 20));
                    remotePort = Port(Marshal.ReadInt32(row, 44));
                    pid = Marshal.ReadInt32(row, 52);
                }
                else
                {
                    localPort = Port(Marshal.ReadInt32(row, 8));
                    remotePort = Port(Marshal.ReadInt32(row, 16));
                    pid = Marshal.ReadInt32(row, 20);
                }
                if (localPort == client.Port && remotePort == serverPort && pid > 0) return pid;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table.Value); }
    }

    public static CallerApp Describe(int processId)
    {
        var path = ImagePath(processId);
        string? name = null;
        if (path is not null)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                name = string.IsNullOrWhiteSpace(version.FileDescription) ? version.ProductName : version.FileDescription;
            }
            catch (FileNotFoundException) { }
        }
        if (string.IsNullOrWhiteSpace(name) && path is not null) name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            try { using var process = Process.GetProcessById(processId); name = process.ProcessName; }
            catch (ArgumentException) { }
        }
        var key = path?.ToLowerInvariant() ?? (name is null ? "unknown" : "process:" + name.ToLowerInvariant());
        return new(key, name?.Trim() ?? CallerApp.Unknown.Name, path, processId);
    }

    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static IntPtr? ReadTable(int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (result == 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (result != ErrorInsufficientBuffer) return null;
        }
        return null;
    }

    private static int Port(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
