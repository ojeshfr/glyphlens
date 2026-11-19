using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Tests.Worker;

public sealed partial class NamedPipeWorkerListenerTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [GeneratedRegex("^glyphlens-[0-9a-f]{32}$")]
    private static partial Regex PipeNamePattern();

    [Fact]
    public void PipeNames_AreRandomAndMatchThePatternTheWorkerAccepts()
    {
        var factory = new NamedPipeWorkerListenerFactory();
        var names = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            using var listener = factory.Create();
            names.Add(listener.PipeName);
        }

        Assert.All(names, name => Assert.Matches(PipeNamePattern(), name));
        Assert.Equal(40, names.Distinct().Count());
    }

    [Fact]
    public void ThePipeAccessList_GrantsOnlyTheCurrentUser_AndDeniesNetworkLogons()
    {
        using var listener = new NamedPipeWorkerListener("glyphlens-" + Guid.NewGuid().ToString("N"));

        var security = listener.GetAccessControl();

        Assert.NotNull(security);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();
        var user = WindowsIdentity.GetCurrent().User!;
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && Equals(r.IdentityReference, network));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && Equals(r.IdentityReference, user)
            && r.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));

        // Nobody else, in particular not Everyone, Users or Authenticated Users, is granted anything.
        var broad = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
        };
        Assert.DoesNotContain(rules, r => broad.Contains(r.IdentityReference));
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void TheDenyEntry_ComesBeforeTheAllowEntry()
    {
        // Windows evaluates explicit deny entries first only when they precede allows; verify the canonical order.
        using var listener = new NamedPipeWorkerListener("glyphlens-" + Guid.NewGuid().ToString("N"));

        var rules = listener.GetAccessControl()!.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.Equal(AccessControlType.Deny, rules[0].AccessControlType);
        Assert.Equal(AccessControlType.Allow, rules[1].AccessControlType);
    }

    [Fact]
    public async Task ACurrentUserClient_CanConnect_AndExchangeBytes()
    {
        using var listener = new NamedPipeWorkerListenerFactory().Create();
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        await using var client = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var server = await accept.WaitAsync(Long);

        // The pipe has no buffer, so a write completes only once the peer reads: read concurrently.
        var buffer = new byte[3];
        var read = server.ReadExactlyAsync(buffer).AsTask();
        await client.WriteAsync(new byte[] { 1, 2, 3 });
        await client.FlushAsync();
        await read.WaitAsync(Long);

        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
    }

    [Fact]
    public async Task OnlyOneClientCanBeConnectedAtATime()
    {
        using var listener = new NamedPipeWorkerListenerFactory().Create();
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        await using var first = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await first.ConnectAsync(5000);
        await using var server = await accept.WaitAsync(Long);

        await using var second = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Assert.ThrowsAsync<TimeoutException>(() => second.ConnectAsync(400));
    }

    [Fact]
    public async Task AfterTheFirstClientIsDropped_TheNextOneCanBeAccepted()
    {
        using var listener = new NamedPipeWorkerListenerFactory().Create();

        // First client (for example an impostor): accepted, then rejected by the caller.
        var firstAccept = listener.AcceptAsync(CancellationToken.None).AsTask();
        await using (var impostor = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await impostor.ConnectAsync(5000);
            await using var impostorServerEnd = await firstAccept.WaitAsync(Long);
        }

        // The real client then connects to a fresh instance.
        var secondAccept = listener.AcceptAsync(CancellationToken.None).AsTask();
        await using var real = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await real.ConnectAsync(5000);
        await using var realServerEnd = await secondAccept.WaitAsync(Long);

        Assert.True(((NamedPipeServerStream)realServerEnd).IsConnected);
    }

    [Fact]
    public async Task ACancelledAccept_LeavesTheListenerUsable()
    {
        using var listener = new NamedPipeWorkerListenerFactory().Create();
        using var cts = new CancellationTokenSource();
        var cancelled = listener.AcceptAsync(cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        await using var client = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        await using var server = await accept.WaitAsync(Long);
        Assert.True(((NamedPipeServerStream)server).IsConnected);
    }

    [Fact]
    public async Task ADisposedListener_RefusesToAccept_AndTheNameDisappears()
    {
        var listener = new NamedPipeWorkerListenerFactory().Create();
        var name = listener.PipeName;

        listener.Dispose();
        listener.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await listener.AcceptAsync(CancellationToken.None));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(300)); // nothing is listening any more
    }
}
