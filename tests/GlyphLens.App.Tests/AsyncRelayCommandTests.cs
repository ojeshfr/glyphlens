using GlyphLens.App.Mvvm;

namespace GlyphLens.App.Tests;

public class AsyncRelayCommandTests
{
    // All commands here run inline (null context) so the tests do not depend on the test framework's ambient context.
    private static AsyncRelayCommand Create(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, canExecute, context: null);

    [Fact]
    public async Task ExecuteAsync_RunsTheDelegate()
    {
        var runs = 0;
        var command = Create(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await command.ExecuteAsync();

        Assert.Equal(1, runs);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task WhileRunning_CannotExecuteAndRaisesCanExecuteChanged()
    {
        var gate = new TaskCompletionSource();
        var command = Create(() => gate.Task);
        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        var running = command.ExecuteAsync();

        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        Assert.Equal(1, raised);

        gate.SetResult();
        await running;

        Assert.True(command.CanExecute(null));
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task ReentrantCalls_AreIgnored()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = Create(() =>
        {
            runs++;
            return gate.Task;
        });

        var first = command.ExecuteAsync();
        await command.ExecuteAsync();
        gate.SetResult();
        await first;

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task CanExecutePredicate_BlocksExecution()
    {
        var allowed = false;
        var runs = 0;
        var command = Create(
            () =>
            {
                runs++;
                return Task.CompletedTask;
            },
            () => allowed);

        await command.ExecuteAsync();
        Assert.Equal(0, runs);
        Assert.False(command.CanExecute(null));

        allowed = true;
        await command.ExecuteAsync();
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AFailingDelegate_PropagatesWhenThereIsNoSynchronizationContext_AndTheCommandRecovers()
    {
        var command = Create(() => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(command.ExecuteAsync);

        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void Constructor_RejectsNullDelegate()
    {
        Assert.Throws<ArgumentNullException>(() => new AsyncRelayCommand(null!));
    }
}
