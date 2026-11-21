using System.Net;
using System.Runtime.InteropServices;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.Windows.Platform;

/// <summary>
/// Reads the system's extended TCP and UDP tables (<c>GetExtendedTcpTable</c>, <c>GetExtendedUdpTable</c>), which list the owning
/// process of every endpoint, for IPv4 and IPv6.
/// </summary>
public sealed class WindowsConnectionProbe : IProcessConnectionProbe
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;
    private const int TcpStateListen = 2;
    private const int TcpStateDeleteTcb = 12;
    private const int MaxRemoteEndpointsReported = 3;

    public ProcessConnections Probe(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        var owners = new HashSet<uint>(processIds.Where(p => p > 0).Select(p => (uint)p));
        int remote = 0;
        int loopback = 0;
        int udp = 0;
        var remoteEndpoints = new List<string>();

        foreach (var row in ReadTcp(AfInet).Concat(ReadTcp(AfInet6)))
        {
            if (!owners.Contains(row.Owner) || row.State is TcpStateListen or TcpStateDeleteTcb)
            {
                continue;
            }

            if (IsLocal(row.RemoteAddress))
            {
                loopback++;
            }
            else
            {
                remote++;
                if (remoteEndpoints.Count < MaxRemoteEndpointsReported)
                {
                    remoteEndpoints.Add($"{row.RemoteAddress}:{row.RemotePort}");
                }
            }
        }

        foreach (uint owner in ReadUdp(AfInet).Concat(ReadUdp(AfInet6)))
        {
            if (owners.Contains(owner))
            {
                udp++;
            }
        }

        return new ProcessConnections(remote, loopback, udp, remoteEndpoints);
    }

    /// <summary>Whether the address is this PC itself (loopback) or unspecified, i.e. not a remote machine.</summary>
    internal static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    private readonly record struct TcpRow(uint Owner, int State, IPAddress RemoteAddress, int RemotePort);

    private static List<TcpRow> ReadTcp(int family)
    {
        var rows = new List<TcpRow>();
        foreach (var buffer in ReadTable(family, tcp: true))
        {
            int count = buffer.Length >= 4 ? BitConverter.ToInt32(buffer, 0) : 0;
            int size = family == AfInet ? 24 : 56;
            for (int i = 0; i < count && 4 + ((i + 1) * size) <= buffer.Length; i++)
            {
                var row = buffer.AsSpan(4 + (i * size), size);
                if (family == AfInet)
                {
                    // state, localAddr, localPort, remoteAddr, remotePort, owningPid
                    int state = BitConverter.ToInt32(row[..4]);
                    var remote = new IPAddress(row.Slice(12, 4));
                    int port = Port(row.Slice(16, 4));
                    uint owner = BitConverter.ToUInt32(row.Slice(20, 4));
                    rows.Add(new TcpRow(owner, state, remote, port));
                }
                else
                {
                    // localAddr[16] 0, localScope 16, localPort 20, remoteAddr[16] 24, remoteScope 40, remotePort 44, state 48, owningPid 52
                    var remote = new IPAddress(row.Slice(24, 16));
                    int port = Port(row.Slice(44, 4));
                    int state = BitConverter.ToInt32(row.Slice(48, 4));
                    uint owner = BitConverter.ToUInt32(row.Slice(52, 4));
                    rows.Add(new TcpRow(owner, state, remote, port));
                }
            }
        }

        return rows;
    }

    private static List<uint> ReadUdp(int family)
    {
        var owners = new List<uint>();
        foreach (var buffer in ReadTable(family, tcp: false))
        {
            int count = buffer.Length >= 4 ? BitConverter.ToInt32(buffer, 0) : 0;
            int size = family == AfInet ? 12 : 28;
            for (int i = 0; i < count && 4 + ((i + 1) * size) <= buffer.Length; i++)
            {
                // IPv4: localAddr, localPort, owningPid. IPv6: localAddr[16], localScope, localPort, owningPid.
                owners.Add(BitConverter.ToUInt32(buffer.AsSpan(4 + (i * size) + size - 4, 4)));
            }
        }

        return owners;
    }

    private static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];

    private static IEnumerable<byte[]> ReadTable(int family, bool tcp)
    {
        // The tables change between the size query and the read; retry a few times with the size the system asked for.
        int size = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint result;
            IntPtr memory = IntPtr.Zero;
            try
            {
                if (size > 0)
                {
                    memory = Marshal.AllocHGlobal(size);
                }

                result = tcp
                    ? GetExtendedTcpTable(memory, ref size, false, family, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(memory, ref size, false, family, UdpTableOwnerPid, 0);

                if (result == NoError && memory != IntPtr.Zero)
                {
                    var copy = new byte[size];
                    Marshal.Copy(memory, copy, 0, size);
                    return [copy];
                }
            }
            finally
            {
                if (memory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(memory);
                }
            }

            if (result != InsufficientBuffer && !(result == NoError && memory == IntPtr.Zero))
            {
                break;
            }

            size += 4096; // headroom for rows that appear between the two calls
        }

        return [];
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
