using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Infrastructure.IPC;

/// <summary>Timeouts and the restart budget of <see cref="WorkerProcessManager"/>.</summary>
public sealed record WorkerManagerOptions
{
    /// <summary>How long the worker has to connect to the pipe after it is launched.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the worker has to answer <c>worker.initialize</c>.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a <c>worker.health</c> request may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the worker has to acknowledge <c>worker.shutdown</c>.</summary>
    public TimeSpan ShutdownRequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a worker may take to exit after acknowledging shutdown before it is killed.</summary>
    public TimeSpan GracefulExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Automatic restarts after unexpected exits before giving up (reset by a stable run or an explicit start).</summary>
    public int MaxRestartAttempts { get; init; } = 5;

    public TimeSpan RestartBackoffInitial { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RestartBackoffMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A worker that ran at least this long before failing does not count against the restart budget.</summary>
    public TimeSpan StableUptime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Clients that fail authentication or the handshake before the launch is abandoned.</summary>
    public int MaxRejectedClients { get; init; } = 3;

    /// <summary>Consecutive unanswered health checks before the worker is treated as hung and restarted.</summary>
    public int MaxConsecutiveHealthTimeouts { get; init; } = 3;
}

/// <summary>
/// Starts, supervises and stops the Python worker and owns its pipe connection.
/// </summary>
/// <remarks>
/// <para>
/// A session = a fresh pipe listener (unique name), a worker process launched with that name and a random session
/// token, an authenticated connection, and the <c>worker.initialize</c> handshake. The worker proves it is the
/// process we launched by echoing the token; clients that fail to do so are disconnected and the pipe accepts again
/// (a bounded number of times).
/// </para>
/// <para>
/// Start/stop/restart are serialised by a gate and never run on the caller's thread for long: all blocking work
/// (interpreter discovery, process start) is moved to the thread pool. Stop cancels an in-flight start or backoff
/// delay first, so it is never stuck behind one. An unexpected exit or lost connection triggers a bounded restart
/// sequence with exponential backoff; nothing restarts during an intentional stop.
/// </para>
/// </remarks>
public sealed class WorkerProcessManager : IWorkerProcessManager, IWorkerRequestChannel, IAsyncDisposable
{
    private readonly IWorkerPipeListenerFactory _listeners;
    private readonly IWorkerProcessLauncher _launcher;
    private readonly IWorkerLog _log;
    private readonly WorkerManagerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private WorkerStatus _status = WorkerStatus.Stopped;
    private string? _lastError;
    private WorkerHealth? _lastHealth;
    private Session? _session;
    private CancellationTokenSource _runCts = new();
    private int _stopEpoch;
    private int _restartAttempts;
    private int _healthTimeouts;
    private volatile bool _stopRequested;
    private bool _disposed;

    public WorkerProcessManager(
        IWorkerPipeListenerFactory listeners,
        IWorkerProcessLauncher launcher,
        WorkerManagerOptions? options = null,
        IWorkerLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(launcher);

        _listeners = listeners;
        _launcher = launcher;
        _options = options ?? new WorkerManagerOptions();
        _log = log ?? NullWorkerLog.Instance;
    }

    public event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;

    public WorkerStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public int? ProcessId => Volatile.Read(ref _session)?.Process.Id;

    public string? LastError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError;
            }
        }
    }

    public WorkerHealth? LastHealth
    {
        get
        {
            lock (_stateLock)
            {
                return _lastHealth;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A Stop that arrives while this call waits for its turn wins: it bumps the epoch, and this start is abandoned.
        var epoch = Volatile.Read(ref _stopEpoch);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopEpoch) != epoch || _disposed)
            {
                throw new OperationCanceledException("The worker was stopped before it could start.");
            }

            if (Status == WorkerStatus.Ready)
            {
                return;
            }

            var run = BeginRun();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
            _restartAttempts = 0;
            await StartSessionAsync(run.Token, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopEpoch);
        _stopRequested = true;
        CancelRun();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stopRequested = true;
        CancelRun();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);

            var run = BeginRun();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
            _restartAttempts = 0;
            await StartSessionAsync(run.Token, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || Status != WorkerStatus.Ready)
        {
            throw new WorkerException(WorkerErrorKind.NotConnected, "The worker is not running.");
        }

        try
        {
            var response = await session.Connection
                .RequestAsync(WorkerMessageTypes.WorkerHealth, null, _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            var health = WorkerHealthParser.ParseHealth(response.Payload);
            Interlocked.Exchange(ref _healthTimeouts, 0);
            lock (_stateLock)
            {
                _lastHealth = health;
            }

            return health;
        }
        catch (WorkerException ex) when (ex.Kind == WorkerErrorKind.RequestTimeout)
        {
            if (Interlocked.Increment(ref _healthTimeouts) >= _options.MaxConsecutiveHealthTimeouts)
            {
                // Detached on purpose: this call's own token must not cancel the recovery.
                _ = Task.Run(() => OnSessionEndedAsync(session, "The worker stopped answering health checks.", _runCts.Token), CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Sends one request over the live worker connection and returns the response payload (<see langword="null"/> when
    /// the worker sent none). This is the channel the translation clients use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It never starts or restarts the worker: when no authenticated connection is live it fails with
    /// <see cref="WorkerErrorKind.NotConnected"/>. A request timeout does not close the connection and does not count
    /// towards the hung-worker detection (a worker that is busy translating is not hung). The worker lifecycle messages
    /// (<c>worker.initialize</c>, <c>worker.shutdown</c>) belong to this manager and are refused here.
    /// </para>
    /// <para>
    /// Payloads are private screen content. They are serialised straight onto the pipe and are never logged or copied
    /// into an exception message.
    /// </para>
    /// </remarks>
    /// <exception cref="WorkerException">
    /// <see cref="WorkerErrorKind.NotConnected"/> (no live connection), <see cref="WorkerErrorKind.Rejected"/> (the worker
    /// answered with an <c>error</c>; see <see cref="WorkerException.ErrorCode"/>), <see cref="WorkerErrorKind.RequestTimeout"/>,
    /// <see cref="WorkerErrorKind.ConnectionLost"/>, <see cref="WorkerErrorKind.TooManyRequests"/> or
    /// <see cref="WorkerErrorKind.ProtocolViolation"/>.
    /// </exception>
    public async Task<JsonElement?> RequestAsync(string messageType, object? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (messageType is WorkerMessageTypes.WorkerInitialize or WorkerMessageTypes.WorkerShutdown)
        {
            throw new ArgumentException("The worker lifecycle messages are sent by the manager itself.", nameof(messageType));
        }

        var session = Volatile.Read(ref _session);
        if (session is null || Status != WorkerStatus.Ready)
        {
            throw new WorkerException(WorkerErrorKind.NotConnected, "The worker is not running.");
        }

        var response = await session.Connection
            .RequestAsync(messageType, payload, timeout, cancellationToken)
            .ConfigureAwait(false);
        return response.Payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WorkerException)
        {
            // Best effort: the job object / pipe loss still ends the worker.
        }

        _gate.Dispose();
        _runCts.Dispose();
    }

    /// <summary>Starts a new run (cancelling the previous one). Must be called with the gate held.</summary>
    private CancellationTokenSource BeginRun()
    {
        CancelRun();
        _stopRequested = false;
        var run = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _runCts, run);
        previous.Dispose();
        return run;
    }

    private void CancelRun()
    {
        try
        {
            _runCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private async Task StartSessionAsync(CancellationToken runToken, CancellationToken cancellationToken)
    {
        SetStatus(WorkerStatus.Starting);
        Session? session = null;
        try
        {
            session = await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            var response = await session.Connection
                .RequestAsync(WorkerMessageTypes.WorkerHealth, null, _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            var health = WorkerHealthParser.ParseHealth(response.Payload);

            Volatile.Write(ref _session, session);
            _healthTimeouts = 0;
            lock (_stateLock)
            {
                _lastHealth = health;
                _lastError = null;
            }

            var started = session;
            session = null; // ownership moved to the field
            SetStatus(WorkerStatus.Ready);
            _ = Task.Run(() => MonitorAsync(started, runToken));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (session is not null)
            {
                await TearDownAsync(session).ConfigureAwait(false);
            }

            if (ex is OperationCanceledException)
            {
                SetStatus(WorkerStatus.Stopped);
                throw;
            }

            var workerEx = ex as WorkerException ?? new WorkerException(WorkerErrorKind.LaunchFailed, "The worker could not be started.", null, ex);
            var unavailable = workerEx.Kind is WorkerErrorKind.PythonNotFound or WorkerErrorKind.WorkerNotFound;
            SetStatus(unavailable ? WorkerStatus.Unavailable : WorkerStatus.Error, workerEx.Message);
            if (ReferenceEquals(workerEx, ex))
            {
                throw;
            }

            throw workerEx;
        }
    }

    private async Task<Session> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var listener = _listeners.Create();
        IWorkerProcess? process = null;
        WorkerConnection? connection = null;
        try
        {
            var spec = new WorkerLaunchSpec(listener.PipeName, token);
            process = await Task.Run(() => _launcher.Launch(spec), cancellationToken).ConfigureAwait(false);
            connection = await AcceptAuthenticatedAsync(listener, process, token, cancellationToken).ConfigureAwait(false);
            return new Session(listener, process, connection, DateTimeOffset.UtcNow);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            listener.Dispose();
            if (process is not null)
            {
                KillQuietly(process);
                process.Dispose();
            }

            throw;
        }
    }

    private async Task<WorkerConnection> AcceptAuthenticatedAsync(
        IWorkerPipeListener listener,
        IWorkerProcess process,
        string token,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ConnectTimeout);

        var rejected = 0;
        while (true)
        {
            var accept = listener.AcceptAsync(deadline.Token).AsTask();
            var finished = await Task.WhenAny(accept, process.Exited).ConfigureAwait(false);
            if (!ReferenceEquals(finished, accept))
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                await ObserveAsync(accept).ConfigureAwait(false);
                throw new WorkerException(WorkerErrorKind.ProcessExited, DescribeExit("The worker exited before connecting", process, await process.Exited.ConfigureAwait(false)));
            }

            Stream stream;
            try
            {
                stream = await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WorkerException(
                    WorkerErrorKind.ConnectTimeout,
                    $"The worker did not connect within {_options.ConnectTimeout.TotalSeconds:0.#} s." + Tail(process));
            }

            var connection = new WorkerConnection(stream, _log);
            connection.Start();
            try
            {
                await HandshakeAsync(connection, token, cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (WorkerException ex) when (ex.Kind != WorkerErrorKind.Rejected && rejected < _options.MaxRejectedClients)
            {
                rejected++;
                _log.Info($"Rejected a pipe client that failed the handshake ({ex.Kind}); waiting for the worker ({rejected}/{_options.MaxRejectedClients}).");
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task HandshakeAsync(WorkerConnection connection, string token, CancellationToken cancellationToken)
    {
        WorkerMessageEnvelope response;
        try
        {
            response = await connection
                .RequestAsync(
                    WorkerMessageTypes.WorkerInitialize,
                    new { clientProtocolVersion = ProtocolVersion.Current.ToString() },
                    _options.InitializeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkerException ex) when (ex.Kind == WorkerErrorKind.Rejected)
        {
            throw new WorkerException(WorkerErrorKind.Rejected, "The worker refused the handshake: " + ex.Message, ex.ErrorCode, ex);
        }

        var echoed = WorkerHealthParser.ParseSessionToken(response.Payload);
        if (echoed is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(echoed), Encoding.UTF8.GetBytes(token)))
        {
            throw new WorkerException(WorkerErrorKind.HandshakeFailed, "The pipe client did not present the session token.");
        }

        var version = WorkerHealthParser.ParseInitializeProtocolVersion(response.Payload);
        if (version is not { } v || !v.IsCompatibleWith(ProtocolVersion.Current))
        {
            throw new WorkerException(WorkerErrorKind.Rejected, "The worker speaks an incompatible protocol version.", "unsupported_protocol_version");
        }
    }

    private async Task MonitorAsync(Session session, CancellationToken runToken)
    {
        var finished = await Task.WhenAny(session.Connection.Closed, session.Process.Exited).ConfigureAwait(false);
        if (_stopRequested || !ReferenceEquals(Volatile.Read(ref _session), session))
        {
            return;
        }

        if (!session.Process.Exited.IsCompleted)
        {
            // A pipe usually closes a moment before the exit code is known; give the process a brief chance to report it
            // so the diagnostics say "exited with code N" instead of just "connection lost".
            await Task.WhenAny(session.Process.Exited, Task.Delay(TimeSpan.FromMilliseconds(500), runToken)).ConfigureAwait(false);
        }

        string reason;
        if (session.Process.Exited.IsCompleted)
        {
            reason = DescribeExit("The worker process exited unexpectedly", session.Process, await session.Process.Exited.ConfigureAwait(false));
        }
        else
        {
            var closed = await ((Task<WorkerConnectionClosed>)finished).ConfigureAwait(false);
            reason = $"The worker connection was lost ({closed.Reason}).";
        }

        await OnSessionEndedAsync(session, reason, runToken).ConfigureAwait(false);
    }

    private async Task OnSessionEndedAsync(Session session, string reason, CancellationToken runToken)
    {
        try
        {
            await _gate.WaitAsync(runToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_stopRequested || !ReferenceEquals(Volatile.Read(ref _session), session))
            {
                return;
            }

            if (DateTimeOffset.UtcNow - session.StartedAt >= _options.StableUptime)
            {
                _restartAttempts = 0;
            }

            // Report the failure before tearing down, so Status never claims Ready for a worker that is gone.
            lock (_stateLock)
            {
                _lastHealth = null;
            }

            SetStatus(WorkerStatus.Error, reason);
            Volatile.Write(ref _session, null);
            await TearDownAsync(session).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await RestartWithBackoffAsync(runToken).ConfigureAwait(false);
    }

    private async Task RestartWithBackoffAsync(CancellationToken runToken)
    {
        while (!_stopRequested && !runToken.IsCancellationRequested)
        {
            if (_restartAttempts >= _options.MaxRestartAttempts)
            {
                SetStatus(WorkerStatus.Unavailable, $"The worker failed {_restartAttempts} times in a row; automatic restart stopped.");
                return;
            }

            var attempt = ++_restartAttempts;
            var delay = TimeSpan.FromTicks(Math.Min(
                _options.RestartBackoffMax.Ticks,
                _options.RestartBackoffInitial.Ticks * (1L << Math.Min(attempt - 1, 20))));
            _log.Info($"Restarting the worker in {delay.TotalSeconds:0.###} s (attempt {attempt}/{_options.MaxRestartAttempts}).");

            try
            {
                await Task.Delay(delay, runToken).ConfigureAwait(false);
                await _gate.WaitAsync(runToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_stopRequested || runToken.IsCancellationRequested || Volatile.Read(ref _session) is not null)
                {
                    return;
                }

                await StartSessionAsync(runToken, runToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WorkerException ex) when (ex.Kind is WorkerErrorKind.PythonNotFound or WorkerErrorKind.WorkerNotFound)
            {
                return; // retrying cannot fix a missing interpreter; the status already says so
            }
            catch (WorkerException)
            {
                // The status reflects the failure; loop until the budget is spent.
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await ShutDownGracefullyAsync(session, cancellationToken).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _lastHealth = null;
        }

        SetStatus(WorkerStatus.Stopped);
    }

    private async Task ShutDownGracefullyAsync(Session session, CancellationToken cancellationToken)
    {
        if (session.Connection.IsOpen && !session.Process.Exited.IsCompleted)
        {
            try
            {
                await session.Connection
                    .RequestAsync(WorkerMessageTypes.WorkerShutdown, null, _options.ShutdownRequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkerException)
            {
                // Not answering is handled by the kill below.
            }
        }

        if (!await WaitForExitAsync(session.Process, _options.GracefulExitTimeout, cancellationToken).ConfigureAwait(false))
        {
            _log.Info("The worker did not exit after shutdown; terminating it.");
            KillQuietly(session.Process);
            await WaitForExitAsync(session.Process, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }

        await TearDownAsync(session).ConfigureAwait(false);
    }

    private static async ValueTask<bool> WaitForExitAsync(IWorkerProcess process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await process.Exited.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task TearDownAsync(Session session)
    {
        // Terminate a still-running process first: a hung worker must not be able to delay recovery by holding the pipe.
        if (!session.Process.Exited.IsCompleted)
        {
            KillQuietly(session.Process);
        }

        await session.Connection.DisposeAsync().ConfigureAwait(false);
        session.Listener.Dispose();
        session.Process.Dispose();
    }

    private static void KillQuietly(IWorkerProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The abandoned accept ended; nothing to report.
        }
    }

    private static string DescribeExit(string prefix, IWorkerProcess process, int exitCode) =>
        $"{prefix} (exit code {exitCode}).{Tail(process)}";

    private static string Tail(IWorkerProcess process)
    {
        var lines = process.RecentErrorOutput;
        return lines.Count == 0 ? string.Empty : " Worker output: " + string.Join(" | ", lines.TakeLast(3));
    }

    private void SetStatus(WorkerStatus next, string? error = null)
    {
        WorkerStatus previous;
        lock (_stateLock)
        {
            previous = _status;
            _status = next;
            if (error is not null)
            {
                _lastError = error;
            }
        }

        if (previous == next)
        {
            return;
        }

        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        var args = new WorkerStatusChangedEventArgs(previous, next, error);
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<WorkerStatusChangedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A faulty subscriber must not break supervision.
            }
        }
    }

    private sealed record Session(IWorkerPipeListener Listener, IWorkerProcess Process, WorkerConnection Connection, DateTimeOffset StartedAt);
}
