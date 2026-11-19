using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

/// <summary>
/// A scriptable <see cref="IScreenCaptureService"/> that records calls and lets a test drive status changes.
/// It follows the real service's state rules so view-model tests exercise realistic sequences.
/// </summary>
internal sealed class FakeCaptureService : IScreenCaptureService
{
    private readonly CaptureStateMachine _state = new();
    private CaptureTarget? _currentTarget;

    public LatestFrameMailbox Mailbox { get; } = new();

    public List<CaptureTarget> StartedTargets { get; } = [];

    public int StopCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    /// <summary>When set, <see cref="StartAsync"/> faults with this error instead of capturing.</summary>
    public CaptureError? FailStartWith { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> waits for this task (or cancellation) before completing.</summary>
    public Task? StartGate { get; set; }

    public CaptureStatus Status => _state.Status;

    public CaptureTarget? CurrentTarget => _currentTarget;

    public IFrameSource Frames => Mailbox;

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    public async Task StartAsync(CaptureTarget target, CancellationToken cancellationToken = default)
    {
        StartedTargets.Add(target);
        Move(CaptureStatus.Starting);

        try
        {
            if (StartGate is not null)
            {
                await StartGate.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Move(CaptureStatus.Idle);
            throw;
        }

        if (FailStartWith is { } error)
        {
            Move(CaptureStatus.Faulted, error);
            throw new CaptureException(error);
        }

        _currentTarget = target;
        Move(CaptureStatus.Capturing);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCalls++;
        if (Status == CaptureStatus.Capturing)
        {
            Move(CaptureStatus.Stopping);
            _currentTarget = null;
            Mailbox.Clear();
            Move(CaptureStatus.Idle);
        }
        else if (Status == CaptureStatus.Faulted)
        {
            Move(CaptureStatus.Idle);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        Mailbox.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Simulates the capture ending on its own (window closed, device lost).</summary>
    public void Fault(CaptureError error)
    {
        _currentTarget = null;
        Mailbox.Clear();
        Move(CaptureStatus.Faulted, error);
    }

    private void Move(CaptureStatus next, CaptureError? error = null)
    {
        var previous = _state.Status;
        _state.TransitionTo(next);
        StatusChanged?.Invoke(this, new CaptureStatusChangedEventArgs(previous, next, error));
    }
}

internal sealed class FakeTargetProvider : ICaptureTargetProvider
{
    public List<CaptureTarget> Targets { get; set; } =
    [
        new("display:1", CaptureTargetKind.Display, "Display 1", 1920, 1080, true, bounds: new ScreenRectangle(0, 0, 1920, 1080), dpi: new DpiScale(1.5)),
        new("display:2", CaptureTargetKind.Display, "Display 2", 1080, 1920, false, bounds: new ScreenRectangle(-1080, 0, 1080, 1920), dpi: new DpiScale(1.0)),
        new("window:1", CaptureTargetKind.Window, "Inbox - Mail", 1280, 720, processName: "chrome"),
        new("window:2", CaptureTargetKind.Window, "Report.pdf", 900, 1200, processName: "SumatraPDF"),
    ];

    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<CaptureTarget> GetTargets()
    {
        Calls++;
        return Failure is null ? [.. Targets] : throw Failure;
    }
}
