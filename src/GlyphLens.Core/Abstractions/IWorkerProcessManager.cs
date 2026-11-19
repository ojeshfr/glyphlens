using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Owns the lifecycle of the local inference worker process and its pipe connection.
/// </summary>
/// <remarks>
/// <para>
/// Implementation: <c>WorkerProcessManager</c> in GlyphLens.Infrastructure. See docs/IPC-PROTOCOL.md.
/// </para>
/// <para>
/// <see cref="WorkerStatus.Ready"/> means the process runs, the pipe is connected and the protocol handshake
/// completed. It does <b>not</b> mean any engine is usable: read <see cref="LastHealth"/> for that.
/// </para>
/// </remarks>
public interface IWorkerProcessManager
{
    WorkerStatus Status { get; }

    /// <summary>The worker's process id while a worker process exists.</summary>
    int? ProcessId { get; }

    /// <summary>A short, sanitised description of the most recent failure, or <see langword="null"/>.</summary>
    string? LastError { get; }

    /// <summary>The most recent health report from the worker, cleared when the worker stops.</summary>
    WorkerHealth? LastHealth { get; }

    event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;

    /// <summary>Starts the worker and completes once it is <see cref="WorkerStatus.Ready"/>.</summary>
    /// <exception cref="WorkerException">The worker could not be started; <see cref="Status"/> then reflects the failure.</exception>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Requests a graceful shutdown, terminating the process if it does not exit in time.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Stops the worker (if running) and starts a fresh one, resetting the automatic-restart budget.</summary>
    Task RestartAsync(CancellationToken cancellationToken);

    /// <summary>Asks the running worker for its health and stores it in <see cref="LastHealth"/>.</summary>
    /// <exception cref="WorkerException">The worker is not connected, did not answer, or answered with an error.</exception>
    Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
}
