using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

/// <summary>A scriptable <see cref="IWorkerProcessManager"/> that records calls and lets a test drive state changes.</summary>
internal sealed class FakeWorkerManager : IWorkerProcessManager
{
    public static WorkerHealth TruthfulHealth(int pid = 4321) => new(
        WorkerComponentState.Ready,
        WorkerComponentState.Connected,
        WorkerComponentState.Initialized,
        WorkerComponentState.NotImplemented,
        WorkerComponentState.NotImplemented,
        WorkerComponentState.NotImplemented,
        WorkerComponentState.NotInstalled,
        "0.2.0",
        ProtocolVersion.Current,
        "3.12.10",
        pid);

    public WorkerStatus Status { get; private set; } = WorkerStatus.Stopped;

    public int? ProcessId { get; private set; }

    public string? LastError { get; private set; }

    public WorkerHealth? LastHealth { get; private set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int RestartCalls { get; private set; }

    public int HealthCalls { get; private set; }

    /// <summary>When set, Start/Restart fail with this exception (and the status becomes Error).</summary>
    public WorkerException? FailStartWith { get; set; }

    /// <summary>When set, Start waits for this task (or cancellation) before completing.</summary>
    public Task? StartGate { get; set; }

    /// <summary>When set, GetHealth fails with this exception.</summary>
    public Exception? FailHealthWith { get; set; }

    public event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StartCalls++;
        Move(WorkerStatus.Starting);
        if (StartGate is not null)
        {
            try
            {
                await StartGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Move(WorkerStatus.Stopped);
                throw;
            }
        }

        if (FailStartWith is { } failure)
        {
            LastError = failure.Message;
            Move(WorkerStatus.Error, failure.Message);
            throw failure;
        }

        ProcessId = 4321;
        LastHealth = TruthfulHealth(4321);
        LastError = null;
        Move(WorkerStatus.Ready);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCalls++;
        ProcessId = null;
        LastHealth = null;
        Move(WorkerStatus.Stopped);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        RestartCalls++;
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        HealthCalls++;
        if (FailHealthWith is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(LastHealth ?? throw new WorkerException(WorkerErrorKind.NotConnected, "The worker is not running."));
    }

    /// <summary>Simulates the worker dying on its own.</summary>
    public void Crash(string reason)
    {
        ProcessId = null;
        LastHealth = null;
        LastError = reason;
        Move(WorkerStatus.Error, reason);
    }

    private void Move(WorkerStatus next, string? reason = null)
    {
        var previous = Status;
        Status = next;
        StatusChanged?.Invoke(this, new WorkerStatusChangedEventArgs(previous, next, reason));
    }
}
