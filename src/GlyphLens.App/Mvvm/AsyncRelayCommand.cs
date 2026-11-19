using System.Windows.Input;

namespace GlyphLens.App.Mvvm;

/// <summary>
/// An <see cref="ICommand"/> that runs an async operation and cannot be re-entered while it is running.
/// </summary>
/// <remarks>
/// The delegate is expected to handle its own failures (view-model methods report errors through state).
/// An exception that escapes is rethrown on the captured synchronisation context so it is never swallowed silently.
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly SynchronizationContext? _context;
    private bool _isRunning;

    /// <summary>Creates a command bound to the current (UI) synchronisation context.</summary>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(execute, canExecute, SynchronizationContext.Current)
    {
    }

    /// <param name="execute">The operation to run.</param>
    /// <param name="canExecute">Optional extra condition; the command can never execute while it is already running.</param>
    /// <param name="context">
    /// Where <see cref="CanExecuteChanged"/> and escaped exceptions are delivered. Exactly this context is used;
    /// <see langword="null"/> means "inline on the calling thread", with exceptions propagating to the caller.
    /// </param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute, SynchronizationContext? context)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _context = context;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>True while the command's operation is executing.</summary>
    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync().ConfigureAwait(true);
    }

    /// <summary>Runs the command and completes when it has finished. Does nothing if it cannot currently execute.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex) when (_context is not null)
        {
            _context.Post(static state => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture((Exception)state!).Throw(), ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null)
        {
            return;
        }

        if (_context is not null && SynchronizationContext.Current != _context)
        {
            _context.Post(_ => handler(this, EventArgs.Empty), null);
        }
        else
        {
            handler(this, EventArgs.Empty);
        }
    }
}
