using GlyphLens.Core.Capture;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Capture;

public class CaptureStateMachineTests
{
    private static readonly (CaptureStatus From, CaptureStatus To)[] LegalTransitions =
    [
        (CaptureStatus.Idle, CaptureStatus.Starting),
        (CaptureStatus.Starting, CaptureStatus.Capturing),
        (CaptureStatus.Starting, CaptureStatus.Faulted),
        (CaptureStatus.Starting, CaptureStatus.Idle),
        (CaptureStatus.Capturing, CaptureStatus.Stopping),
        (CaptureStatus.Capturing, CaptureStatus.Faulted),
        (CaptureStatus.Stopping, CaptureStatus.Idle),
        (CaptureStatus.Faulted, CaptureStatus.Starting),
        (CaptureStatus.Faulted, CaptureStatus.Idle),
    ];

    public static TheoryData<CaptureStatus, CaptureStatus> AllPairs()
    {
        var data = new TheoryData<CaptureStatus, CaptureStatus>();
        foreach (var from in Enum.GetValues<CaptureStatus>())
        {
            foreach (var to in Enum.GetValues<CaptureStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Fact]
    public void NewMachine_StartsIdle()
    {
        Assert.Equal(CaptureStatus.Idle, new CaptureStateMachine().Status);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void IsValidTransition_AllowsExactlyTheDocumentedTransitions(CaptureStatus from, CaptureStatus to)
    {
        Assert.Equal(LegalTransitions.Contains((from, to)), CaptureStateMachine.IsValidTransition(from, to));
    }

    [Fact]
    public void NormalLifecycle_WalksIdleStartingCapturingStoppingIdle()
    {
        var machine = new CaptureStateMachine();

        Assert.Equal(CaptureStatus.Starting, machine.TransitionTo(CaptureStatus.Starting));
        Assert.Equal(CaptureStatus.Capturing, machine.TransitionTo(CaptureStatus.Capturing));
        Assert.Equal(CaptureStatus.Stopping, machine.TransitionTo(CaptureStatus.Stopping));
        Assert.Equal(CaptureStatus.Idle, machine.TransitionTo(CaptureStatus.Idle));
    }

    [Fact]
    public void FaultedMachine_CanBeRestartedRepeatedly()
    {
        var machine = new CaptureStateMachine();

        for (var i = 0; i < 3; i++)
        {
            machine.TransitionTo(CaptureStatus.Starting);
            machine.TransitionTo(CaptureStatus.Faulted);
        }

        machine.TransitionTo(CaptureStatus.Starting);
        machine.TransitionTo(CaptureStatus.Capturing);
        Assert.Equal(CaptureStatus.Capturing, machine.Status);
    }

    [Fact]
    public void TransitionTo_IllegalTransition_ThrowsAndKeepsState()
    {
        var machine = new CaptureStateMachine();

        var ex = Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(CaptureStatus.Capturing));

        Assert.Contains("Idle", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Capturing", ex.Message, StringComparison.Ordinal);
        Assert.Equal(CaptureStatus.Idle, machine.Status);
    }

    [Fact]
    public void TryTransitionTo_ReportsWhetherTheTransitionHappened()
    {
        var machine = new CaptureStateMachine();

        Assert.False(machine.TryTransitionTo(CaptureStatus.Stopping));
        Assert.Equal(CaptureStatus.Idle, machine.Status);
        Assert.True(machine.TryTransitionTo(CaptureStatus.Starting));
        Assert.Equal(CaptureStatus.Starting, machine.Status);
    }

    [Fact]
    public void CannotStartWhileCapturingOrStopping()
    {
        Assert.False(CaptureStateMachine.IsValidTransition(CaptureStatus.Capturing, CaptureStatus.Starting));
        Assert.False(CaptureStateMachine.IsValidTransition(CaptureStatus.Stopping, CaptureStatus.Starting));
    }
}
