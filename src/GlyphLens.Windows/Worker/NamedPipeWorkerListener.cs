using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.Windows.Worker;

/// <summary>Creates <see cref="NamedPipeWorkerListener"/>s with unique, unguessable pipe names.</summary>
public sealed class NamedPipeWorkerListenerFactory : IWorkerPipeListenerFactory
{
    public IWorkerPipeListener Create()
    {
        // 128 random bits: the name is never derived from or accepted from outside the process.
        var name = "glyphlens-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new NamedPipeWorkerListener(name);
    }
}

/// <summary>
/// The application end of the worker pipe: one server instance, byte mode, restricted to the current user.
/// </summary>
/// <remarks>
/// <para>Access control (explicit DACL, replacing the default one that also grants broad read access):</para>
/// <list type="number">
/// <item>Deny <c>NT AUTHORITY\NETWORK</c> everything, so a connection arriving over the network redirector (which
/// carries a network logon token) is refused even though it authenticates as the same user.</item>
/// <item>Allow the current user full control (needed to create the next instance after a rejected client).</item>
/// </list>
/// <para>
/// <c>maxNumberOfServerInstances</c> is 1, so there is at most one worker client at a time. The first instance is
/// created in the constructor, so the pipe exists before the worker is launched. A client that fails authentication
/// is disconnected by the caller, after which <see cref="AcceptAsync"/> creates the next instance.
/// </para>
/// </remarks>
public sealed class NamedPipeWorkerListener : IWorkerPipeListener
{
    private readonly object _gate = new();
    private NamedPipeServerStream? _pending;
    private bool _disposed;

    internal NamedPipeWorkerListener(string pipeName)
    {
        PipeName = pipeName;
        _pending = CreateInstance(pipeName);
    }

    public string PipeName { get; }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            server = _pending ?? CreateInstance(PipeName);
            _pending = server;
        }

        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, server))
                {
                    _pending = null;
                }
            }

            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_gate)
        {
            // Ownership passes to the caller; the next accept creates a fresh instance.
            _pending = null;
        }

        return server;
    }

    public void Dispose()
    {
        NamedPipeServerStream? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
            _pending = null;
        }

        pending?.Dispose();
    }

    /// <summary>The DACL currently applied to the pending pipe instance (for verification in tests).</summary>
    internal PipeSecurity? GetAccessControl()
    {
        lock (_gate)
        {
            return _pending?.GetAccessControl();
        }
    }

    internal static PipeSecurity BuildSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(user);
        return security;
    }

    private static NamedPipeServerStream CreateInstance(string name) =>
        NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            BuildSecurity());
}
