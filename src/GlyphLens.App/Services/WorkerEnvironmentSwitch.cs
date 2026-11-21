using System.IO;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Services;

/// <summary>
/// Moves the worker onto the environment the one-click setup installed: the launcher forgets the interpreter it found at the first
/// start (it is otherwise kept for the whole session), and the worker is restarted so the interpreter is looked up again.
/// </summary>
public sealed class WorkerEnvironmentSwitch : IWorkerEnvironmentSwitch
{
    private readonly IWorkerProcessManager _worker;
    private readonly Func<string?> _currentInterpreter;
    private readonly Action _forgetInterpreter;

    /// <param name="worker">The worker lifecycle.</param>
    /// <param name="currentInterpreter">The interpreter the launcher uses now (<see langword="null"/> before the first start).</param>
    /// <param name="forgetInterpreter">Makes the launcher look for the interpreter again at the next start.</param>
    public WorkerEnvironmentSwitch(IWorkerProcessManager worker, Func<string?> currentInterpreter, Action forgetInterpreter)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(currentInterpreter);
        ArgumentNullException.ThrowIfNull(forgetInterpreter);
        _worker = worker;
        _currentInterpreter = currentInterpreter;
        _forgetInterpreter = forgetInterpreter;
    }

    public async Task<WorkerSwitchResult> UseInterpreterAsync(string pythonPath, bool restart, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);
        if (!restart && _worker.Status == WorkerStatus.Ready && SamePath(_currentInterpreter(), pythonPath))
        {
            return new WorkerSwitchResult(_currentInterpreter(), Restarted: false);
        }

        _forgetInterpreter();
        if (_worker.Status is WorkerStatus.Ready or WorkerStatus.Starting)
        {
            await _worker.RestartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _worker.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        return new WorkerSwitchResult(_currentInterpreter(), Restarted: true);
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
