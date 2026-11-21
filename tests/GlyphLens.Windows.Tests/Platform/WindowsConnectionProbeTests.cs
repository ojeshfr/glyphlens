using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GlyphLens.Windows.Platform;

namespace GlyphLens.Windows.Tests.Platform;

public sealed class WindowsConnectionProbeTests
{
    private static readonly int Me = Environment.ProcessId;

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.4.3", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("2001:db8::1", false)]
    public void IsLocal_OnlyForThisPcItself(string address, bool expected) =>
        Assert.Equal(expected, WindowsConnectionProbe.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void NoProcesses_HaveNoConnections()
    {
        var result = new WindowsConnectionProbe().Probe([]);

        Assert.Equal(0, result.RemoteTcp);
        Assert.Equal(0, result.LoopbackTcp);
        Assert.Equal(0, result.UdpEndpoints);
    }

    [Fact]
    public void ALoopbackConnectionOfThisProcess_IsCountedAsLoopback_NotRemote()
    {
        var probe = new WindowsConnectionProbe();
        var before = probe.Probe([Me]);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = listener.AcceptTcpClient();
        var after = probe.Probe([Me]);

        Assert.True(after.LoopbackTcp >= before.LoopbackTcp + 2, $"loopback {before.LoopbackTcp} -> {after.LoopbackTcp}");
        Assert.Equal(before.RemoteTcp, after.RemoteTcp);
    }

    [Fact]
    public void AConnectionToTheMachinesOwnNetworkAddress_IsCountedAsRemote_WithItsEndpoint()
    {
        // No internet is needed: a connection to this PC's own LAN address looks like a remote one to the probe, which is the case to detect.
        var lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is null)
        {
            return; // this machine has no network address: the case cannot be produced here (not counted as a pass of the remote case)
        }

        var probe = new WindowsConnectionProbe();
        var before = probe.Probe([Me]);

        using var listener = new TcpListener(lan, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(lan, port);
        using var accepted = listener.AcceptTcpClient();
        var after = probe.Probe([Me]);

        Assert.True(after.RemoteTcp >= before.RemoteTcp + 2, $"remote {before.RemoteTcp} -> {after.RemoteTcp}");
        Assert.Contains(after.RemoteEndpoints, e => e.StartsWith(lan.ToString(), StringComparison.Ordinal));
        Assert.True(after.RemoteEndpoints.Count <= 3);
    }

    [Fact]
    public void AnOpenUdpSocket_IsReported()
    {
        var probe = new WindowsConnectionProbe();
        var before = probe.Probe([Me]);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var after = probe.Probe([Me]);

        Assert.True(after.UdpEndpoints >= before.UdpEndpoints + 1);
    }

    [Fact]
    public void AnotherProcess_IsNotCounted_ForThisOne()
    {
        var probe = new WindowsConnectionProbe();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = listener.AcceptTcpClient();

        var other = probe.Probe([int.MaxValue - 1]);

        Assert.Equal(0, other.LoopbackTcp);
        Assert.Equal(0, other.RemoteTcp);
    }
}
