using System.Windows.Input;

namespace GlyphLens.App.Mvvm;

/// <summary>A synchronous <see cref="ICommand"/> whose parameter is a typed item (for example the card a button belongs to).</summary>
public sealed class RelayCommand<T> : ICommand
    where T : class
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter as T) ?? true;

    public void Execute(object? parameter) => _execute(parameter as T);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
