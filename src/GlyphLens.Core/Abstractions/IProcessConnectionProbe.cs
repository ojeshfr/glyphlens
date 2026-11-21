namespace GlyphLens.Core.Abstractions;

/// <summary>What a set of processes is doing on the network, at the moment of asking.</summary>
/// <param name="RemoteTcp">TCP connections (not listeners) whose remote address is not this PC's loopback.</param>
/// <param name="LoopbackTcp">TCP connections between local endpoints (the pipe-like link to the worker counts here if it used TCP; it does not: it uses a named pipe).</param>
/// <param name="UdpEndpoints">UDP endpoints the processes have open (UDP has no connection, so each is reported for the user to judge).</param>
/// <param name="RemoteEndpoints">Up to three of the remote TCP endpoints found (for showing what a failing check saw).</param>
public sealed record ProcessConnections(int RemoteTcp, int LoopbackTcp, int UdpEndpoints, IReadOnlyList<string> RemoteEndpoints);

/// <summary>Asks the operating system which network connections belong to given processes.</summary>
/// <remarks>
/// <para>
/// This is evidence, not a guarantee: it sees what exists when it is called. A connection opened and closed between two calls is
/// not seen, and a process that is not listed is not examined. GlyphLens uses it to show that its own processes had no remote
/// connections when checked; the translation engines are additionally forbidden to reach the network by the worker's guard.
/// </para>
/// </remarks>
public interface IProcessConnectionProbe
{
    /// <summary>Reads the TCP and UDP tables and summarises the connections owned by <paramref name="processIds"/>.</summary>
    ProcessConnections Probe(IReadOnlyCollection<int> processIds);
}
