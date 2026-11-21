using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.App.ViewModels;

/// <summary>One line of the readiness checklist.</summary>
/// <param name="Id">A stable identifier (also the automation id suffix).</param>
/// <param name="Title">What is being checked.</param>
/// <param name="Detail">What was found, in plain words.</param>
/// <param name="Level">How it reflects on readiness.</param>
/// <param name="ActionText">The label of the one real action that helps, or <see langword="null"/>.</param>
/// <param name="Action">What the action does.</param>
public sealed record ReadinessItem(string Id, string Title, string Detail, StatusLevel Level, string? ActionText = null, ICommand? Action = null)
{
    public bool HasAction => Action is not null;

    public string AutomationName => $"{Title}: {Detail}";
}

/// <summary>
/// The first-run checklist: is everything needed to translate Chinese on screen actually in place, as far as GlyphLens can tell.
/// </summary>
/// <remarks>
/// <para>
/// Every row is derived from something that was measured or reported: the worker's lifecycle and its health report, the models
/// the worker lists, the capture status, and a check of the network connections owned by GlyphLens' own processes. A row says
/// "not checked yet" rather than guessing when the worker has not been asked. Actions are real (start the worker, open the OCR
/// models folder, import a model file); nothing is downloaded, installed or changed on the system.
/// </para>
/// </remarks>
public sealed class ReadinessViewModel : ViewModelBase, IDisposable
{
    private readonly TranslationViewModel _translation;
    private readonly OcrViewModel _ocr;
    private readonly CaptureViewModel _capture;
    private readonly IWorkerProcessManager _worker;
    private readonly IProcessConnectionProbe? _probe;
    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly ObservableCollection<ReadinessItem> _items = [];
    private ProcessConnections? _connections;
    private DateTimeOffset? _connectionsCheckedAt;
    private string _summary = string.Empty;
    private string _summaryDetail = string.Empty;
    private StatusLevel _summaryLevel = StatusLevel.Neutral;
    private int _probing;
    private bool _disposed;

    public ReadinessViewModel(
        TranslationViewModel translation,
        OcrViewModel ocr,
        CaptureViewModel capture,
        IWorkerProcessManager worker,
        IProcessConnectionProbe? probe,
        SynchronizationContext? uiContext,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(worker);

        _translation = translation;
        _ocr = ocr;
        _capture = capture;
        _worker = worker;
        _probe = probe;
        _ui = uiContext;
        _time = timeProvider ?? TimeProvider.System;

        Items = new ReadOnlyObservableCollection<ReadinessItem>(_items);
        RecheckCommand = new AsyncRelayCommand(RecheckAsync, () => _probe is not null, uiContext);

        _translation.PropertyChanged += OnSourcePropertyChanged;
        _ocr.PropertyChanged += OnSourcePropertyChanged;
        _capture.PropertyChanged += OnSourcePropertyChanged;
        _worker.StatusChanged += OnWorkerStatusChanged;

        Refresh();
    }

    /// <summary>The checklist, in the order shown.</summary>
    public ReadOnlyObservableCollection<ReadinessItem> Items { get; }

    /// <summary>Re-reads the worker's health and the network connections of GlyphLens' own processes.</summary>
    public AsyncRelayCommand RecheckCommand { get; }

    /// <summary>One sentence about whether Chinese translation can work now.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>What is missing or not yet checked.</summary>
    public string SummaryDetail
    {
        get => _summaryDetail;
        private set => SetProperty(ref _summaryDetail, value);
    }

    public StatusLevel SummaryLevel
    {
        get => _summaryLevel;
        private set => SetProperty(ref _summaryLevel, value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _translation.PropertyChanged -= OnSourcePropertyChanged;
        _ocr.PropertyChanged -= OnSourcePropertyChanged;
        _capture.PropertyChanged -= OnSourcePropertyChanged;
        _worker.StatusChanged -= OnWorkerStatusChanged;
    }

    /// <summary>Rebuilds the rows from the current state (cheap; no process is probed).</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var rows = Build();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= _items.Count)
            {
                _items.Add(rows[i]);
            }
            else if (!_items[i].Equals(rows[i]))
            {
                _items[i] = rows[i];
            }
        }

        while (_items.Count > rows.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        Summarise(rows);
    }

    // ---- rows --------------------------------------------------------------------------------------------------------

    private List<ReadinessItem> Build()
    {
        var status = _worker.Status;
        var health = status == WorkerStatus.Ready ? _worker.LastHealth : null;

        var python = status switch
        {
            WorkerStatus.Ready when health?.PythonVersion is { Length: > 0 } version =>
                new ReadinessItem("python", "Python runtime", $"Python {version}", StatusLevel.Ok),
            WorkerStatus.Ready => new ReadinessItem("python", "Python runtime", "Running (version not reported)", StatusLevel.Ok),
            WorkerStatus.Starting => new ReadinessItem("python", "Python runtime", "Starting…", StatusLevel.Neutral),
            WorkerStatus.Unavailable => new ReadinessItem("python", "Python runtime", _worker.LastError ?? "No Python runtime was found for the worker.", StatusLevel.Problem),
            WorkerStatus.Error => new ReadinessItem("python", "Python runtime", "The worker stopped unexpectedly.", StatusLevel.Problem),
            _ => new ReadinessItem("python", "Python runtime", "Checked when the worker starts.", StatusLevel.Neutral),
        };

        ICommand? startCommand = _translation.StartWorkerCommand;
        bool canStart = _translation.CanStartWorker;
        var workerRow = status switch
        {
            WorkerStatus.Ready => new ReadinessItem("worker", "Worker", $"Running and connected (protocol v{health?.ProtocolVersion})", StatusLevel.Ok),
            WorkerStatus.Starting => new ReadinessItem("worker", "Worker", "Starting…", StatusLevel.Neutral),
            WorkerStatus.Error => new ReadinessItem("worker", "Worker", _worker.LastError ?? "The worker stopped.", StatusLevel.Problem, canStart ? "Start worker" : null, canStart ? startCommand : null),
            WorkerStatus.Unavailable => new ReadinessItem("worker", "Worker", _worker.LastError ?? "The worker could not be started.", StatusLevel.Problem, canStart ? "Start worker" : null, canStart ? startCommand : null),
            _ => new ReadinessItem("worker", "Worker", "Not started. It starts when translation is turned on.", StatusLevel.Neutral, canStart ? "Start worker" : null, canStart ? startCommand : null),
        };

        var ocrRow = LocalOcrAdvisor.Classify(health) switch
        {
            LocalOcrState.Ready => new ReadinessItem("ocr", "Chinese OCR", "PP-OCRv5 is ready (local, offline)", StatusLevel.Ok),
            LocalOcrState.ModelsMissing => new ReadinessItem("ocr", "Chinese OCR", "The OCR model files are not installed.", StatusLevel.Problem, "Open OCR folder", _ocr.OpenOcrFolderCommand),
            LocalOcrState.RuntimeMissing => new ReadinessItem("ocr", "Chinese OCR", "The worker's Python environment lacks the OCR packages (PaddleOCR, ONNX Runtime).", StatusLevel.Problem),
            _ => new ReadinessItem("ocr", "Chinese OCR", status == WorkerStatus.Ready ? "Not reported by the worker." : "Checked once the worker runs.", StatusLevel.Neutral),
        };

        return
        [
            python,
            workerRow,
            ocrRow,
            ModelRow("model-zh", "Simplified Chinese → English model", "zh", "zh_en", status),
            ModelRow("model-zt", "Traditional Chinese → English model (optional)", "zt", "zt_en", status),
            NetworkRow(),
            CaptureRow(),
        ];
    }

    private ReadinessItem ModelRow(string id, string title, string source, string packageKey, WorkerStatus status)
    {
        if (status != WorkerStatus.Ready)
        {
            return new ReadinessItem(id, title, "Checked once the worker runs.", StatusLevel.Neutral);
        }

        if (!_translation.IsCatalogLoaded)
        {
            return new ReadinessItem(id, title, "Reading the installed models…", StatusLevel.Neutral);
        }

        if (_translation.InstalledSources.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            string version = _translation.Packages.FirstOrDefault(p => p.PackageName.Contains(packageKey, StringComparison.OrdinalIgnoreCase))?.Version ?? string.Empty;
            return new ReadinessItem(id, title, version.Length > 0 ? $"Installed (version {version})" : "Installed", StatusLevel.Ok);
        }

        bool optional = id == "model-zt";
        return new ReadinessItem(
            id,
            title,
            "Not installed. Import the .argosmodel file you downloaded.",
            optional ? StatusLevel.Neutral : StatusLevel.Attention,
            _translation.CanImportModel ? "Import model…" : null,
            _translation.CanImportModel ? _translation.ImportModelCommand : null);
    }

    private ReadinessItem NetworkRow()
    {
        const string title = "Offline check";
        if (_probe is null)
        {
            return new ReadinessItem("network", title, "Not available on this system.", StatusLevel.Neutral);
        }

        if (_connections is null || _connectionsCheckedAt is null)
        {
            return new ReadinessItem("network", title, "Not checked yet.", StatusLevel.Neutral, "Check now", RecheckCommand);
        }

        string when = _connectionsCheckedAt.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var c = _connections;
        if (c.RemoteTcp > 0)
        {
            return new ReadinessItem(
                "network",
                title,
                string.Create(CultureInfo.InvariantCulture, $"{c.RemoteTcp} remote connection{(c.RemoteTcp == 1 ? string.Empty : "s")} found ({string.Join(", ", c.RemoteEndpoints)}) at {when}."),
                StatusLevel.Problem,
                "Check again",
                RecheckCommand);
        }

        string udp = c.UdpEndpoints > 0
            ? string.Create(CultureInfo.InvariantCulture, $" {c.UdpEndpoints} local UDP endpoint{(c.UdpEndpoints == 1 ? string.Empty : "s")} open (UDP has no remote peer to list).")
            : string.Empty;
        return new ReadinessItem(
            "network",
            title,
            $"No remote connections from GlyphLens or its worker when checked at {when}.{udp}",
            c.UdpEndpoints > 0 ? StatusLevel.Attention : StatusLevel.Ok,
            "Check again",
            RecheckCommand);
    }

    private ReadinessItem CaptureRow()
    {
        const string title = "Screen capture";
        return _capture.Status switch
        {
            CaptureStatus.Capturing => new ReadinessItem("capture", title, string.IsNullOrEmpty(_capture.TargetInfoText) ? "Capturing" : $"Capturing {_capture.TargetInfoText}", StatusLevel.Ok),
            CaptureStatus.Faulted => new ReadinessItem("capture", title, _capture.ErrorMessage ?? "Capture failed.", StatusLevel.Problem),
            CaptureStatus.Starting or CaptureStatus.Stopping => new ReadinessItem("capture", title, "Changing…", StatusLevel.Neutral),
            _ => new ReadinessItem("capture", title, "Not capturing. Choose a window or display on the Capture page.", StatusLevel.Neutral),
        };
    }

    private void Summarise(IReadOnlyList<ReadinessItem> rows)
    {
        // What Chinese translation needs: the worker, the local OCR, and the Simplified model. The rest informs.
        string[] required = ["worker", "ocr", "model-zh"];
        var needed = rows.Where(r => required.Contains(r.Id)).ToList();
        var notOk = needed.Where(r => r.Level != StatusLevel.Ok).ToList();

        if (notOk.Count == 0)
        {
            SummaryLevel = StatusLevel.Ok;
            Summary = "Ready to translate Simplified Chinese.";
            SummaryDetail = rows.Any(r => r.Level == StatusLevel.Problem)
                ? "Something else needs a look: " + string.Join(", ", rows.Where(r => r.Level == StatusLevel.Problem).Select(r => r.Title)) + "."
                : "The worker, the local OCR and the Simplified Chinese model are in place.";
            return;
        }

        bool anyProblem = notOk.Any(r => r.Level is StatusLevel.Problem or StatusLevel.Attention);
        SummaryLevel = anyProblem ? StatusLevel.Attention : StatusLevel.Neutral;
        Summary = anyProblem ? "Setup needed before Chinese can be translated." : "Not checked yet.";
        SummaryDetail = anyProblem
            ? "Needs attention: " + string.Join(", ", notOk.Where(r => r.Level is StatusLevel.Problem or StatusLevel.Attention).Select(r => r.Title)) + "."
            : "Start the worker (turn translation on) so GlyphLens can check the OCR and the models.";
    }

    // ---- network probe -----------------------------------------------------------------------------------------------

    private async Task RecheckAsync()
    {
        if (_probe is null || Interlocked.Exchange(ref _probing, 1) != 0)
        {
            return;
        }

        try
        {
            var ids = new HashSet<int> { Environment.ProcessId };
            if (_worker.ProcessId is { } launcher)
            {
                _ = ids.Add(launcher);
            }

            if (_worker.LastHealth?.ProcessId is { } pid)
            {
                _ = ids.Add(pid);
            }

            // Reading the system tables is quick, but never on the UI thread.
            var result = await Task.Run(() => _probe.Probe(ids)).ConfigureAwait(false);
            Post(() =>
            {
                _connections = result;
                _connectionsCheckedAt = _time.GetUtcNow();
                Refresh();
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Connection probe failed ({ex.GetType().Name}).");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _probing, 0);
        }
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslationViewModel.WorkerStatus) or nameof(TranslationViewModel.InstalledSources) or nameof(TranslationViewModel.IsCatalogLoaded)
            or nameof(TranslationViewModel.CanStartWorker) or nameof(TranslationViewModel.CanImportModel)
            or nameof(CaptureViewModel.Status) or nameof(CaptureViewModel.TargetInfoText) or nameof(CaptureViewModel.ErrorMessage))
        {
            Refresh();
        }
    }

    private void OnWorkerStatusChanged(object? sender, Core.Events.WorkerStatusChangedEventArgs args)
    {
        Post(() =>
        {
            Refresh();
            if (_worker.Status == WorkerStatus.Ready)
            {
                _ = RecheckCommand.ExecuteAsync(); // the worker just became a process of ours: look at its connections too
            }
        });
    }
}
