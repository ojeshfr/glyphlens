using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Owns the lifecycle of the local inference worker process (GlyphLens.Worker.exe).
/// </summary>
/// <remarks>Planned implementation: GlyphLens.Infrastructure (IPC). See docs/IPC-PROTOCOL.md.</remarks>
public interface IWorkerProcessManager
{
    WorkerStatus Status { get; }

    event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;

    /// <summary>Starts the worker and completes once it is <see cref="WorkerStatus.Ready"/> or has failed.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Requests a graceful shutdown, terminating the process if it does not exit in time.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
