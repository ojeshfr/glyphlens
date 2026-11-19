using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// State and commands of the worker diagnostics panel: start, stop and restart the Python worker, and show what it
/// reports about itself.
/// </summary>
/// <remarks>
/// <para>
/// Every value shown is read from the <see cref="IWorkerProcessManager"/> or from the worker's own health report;
/// nothing is assumed. In particular the engine rows come from the worker and read NOT_IMPLEMENTED in this build: a
/// running worker is never presented as a working OCR or translation engine.
/// </para>
/// <para>
/// Manager callbacks arrive on arbitrary threads and are marshalled to the synchronisation context captured at
/// construction (the UI thread in the application; inline in unit tests). Commands never block the UI thread.
/// </para>
/// </remarks>
public sealed class WorkerViewModel : ViewModelBase, IDisposable
{
    private const string None = "—";

    private readonly IWorkerProcessManager _manager;
    private readonly SynchronizationContext? _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private int _busy;
    private string? _commandError;
    private bool _disposed;

    /// <summary>Creates the view model on the UI thread; manager callbacks are marshalled to that thread's context.</summary>
    public WorkerViewModel(IWorkerProcessManager manager)
        : this(manager, SynchronizationContext.Current)
    {
    }

    /// <param name="manager">The worker lifecycle owner.</param>
    /// <param name="uiContext">
    /// Where manager callbacks are delivered. Exactly this context is used; <see langword="null"/> means "inline on the
    /// calling thread" (it does not fall back to the ambient context).
    /// </param>
    public WorkerViewModel(IWorkerProcessManager manager, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(manager);

        _manager = manager;
        _ui = uiContext;

        StartCommand = new AsyncRelayCommand(() => RunAsync(StartAsync), () => CanStart, uiContext);
        StopCommand = new AsyncRelayCommand(() => RunAsync(StopAsync), () => CanStop, uiContext);
        RestartCommand = new AsyncRelayCommand(() => RunAsync(RestartAsync), () => CanRestart, uiContext);
        RefreshHealthCommand = new AsyncRelayCommand(() => RunAsync(RefreshHealthAsync), () => CanRefreshHealth, uiContext);

        _manager.StatusChanged += OnManagerStatusChanged;
    }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand RefreshHealthCommand { get; }

    public WorkerStatus Status => _manager.Status;

    public string StatusText => Status switch
    {
        WorkerStatus.Stopped => "Stopped",
        WorkerStatus.Starting => "Starting…",
        WorkerStatus.Ready => "Ready",
        WorkerStatus.Error => "Error",
        WorkerStatus.Unavailable => "Unavailable",
        _ => Status.ToString(),
    };

    /// <summary>The IPC link as the application sees it: connected exactly while the worker is Ready.</summary>
    public string IpcText => Status == WorkerStatus.Ready ? "CONNECTED" : "NOT CONNECTED";

    public string ProcessIdText => _manager.ProcessId is { } pid ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) : None;

    // The rows below are the worker's own report (worker.health), upper snake case as documented.
    public string WorkerProcessText => Row(h => h.Process);

    public string WorkerIpcText => Row(h => h.Ipc);

    public string ProtocolStateText => Row(h => h.Protocol);

    public string OcrText => Row(h => h.Ocr);

    public string LanguageDetectionText => Row(h => h.LanguageDetection);

    public string TranslationText => Row(h => h.Translation);

    public string ModelsText => Row(h => h.Models);

    /// <summary>The handshake state with the negotiated protocol version, e.g. <c>INITIALIZED · v1.0</c>, or a dash when unknown.</summary>
    public string ProtocolSummaryText => _manager.LastHealth is { } health
        ? $"{health.Protocol.ToDisplayString()} · v{health.ProtocolVersion}"
        : None;

    /// <summary>Worker and Python versions for the panel footer, or a plain statement when no worker is running.</summary>
    public string RuntimeSummaryText => _manager.LastHealth is { } health
        ? $"Worker {health.WorkerVersion} · Python {health.PythonVersion ?? "unknown"}"
        : "No worker running";

    public string ProtocolVersionText => _manager.LastHealth?.ProtocolVersion.ToString() ?? None;

    public string WorkerVersionText => _manager.LastHealth?.WorkerVersion ?? None;

    public string PythonVersionText => _manager.LastHealth?.PythonVersion ?? None;

    /// <summary>The most recent failure (a start failure, a crash, or a failed command), or <see langword="null"/>.</summary>
    public string? LastError => _commandError ?? _manager.LastError;

    public bool HasError => !string.IsNullOrEmpty(LastError);

    public bool CanStart => !IsBusy && Status is WorkerStatus.Stopped or WorkerStatus.Error or WorkerStatus.Unavailable;

    public bool CanStop => !IsBusy && Status != WorkerStatus.Stopped;

    public bool CanRestart => !IsBusy && Status is WorkerStatus.Ready or WorkerStatus.Error or WorkerStatus.Unavailable;

    public bool CanRefreshHealth => !IsBusy && Status == WorkerStatus.Ready;

    private bool IsBusy => Volatile.Read(ref _busy) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnManagerStatusChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Re-reads everything from the manager and notifies the view.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        RefreshHealthCommand.RaiseCanExecuteChanged();
    }

    private string Row(Func<WorkerHealth, WorkerComponentState> select) =>
        _manager.LastHealth is { } health ? select(health).ToDisplayString() : None;

    private async Task RunAsync(Func<Task> operation)
    {
        Interlocked.Increment(ref _busy);
        _commandError = null;
        Refresh();
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by closing the application or by Stop; not an error.
        }
        catch (WorkerException ex)
        {
            _commandError = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _commandError = $"The worker operation failed unexpectedly ({ex.GetType().Name}).";
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Refresh();
        }
    }

    private Task StartAsync() => _manager.StartAsync(_lifetime.Token);

    private Task StopAsync() => _manager.StopAsync(_lifetime.Token);

    private Task RestartAsync() => _manager.RestartAsync(_lifetime.Token);

    private async Task RefreshHealthAsync() => _ = await _manager.GetHealthAsync(_lifetime.Token).ConfigureAwait(true);

    private void OnManagerStatusChanged(object? sender, WorkerStatusChangedEventArgs args)
    {
        if (_ui is null)
        {
            ApplyStatusChange();
        }
        else
        {
            _ui.Post(_ => ApplyStatusChange(), null);
        }
    }

    private void ApplyStatusChange()
    {
        if (!_disposed)
        {
            Refresh();
        }
    }
}
