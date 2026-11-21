using System.Collections.ObjectModel;
using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Setup;

namespace GlyphLens.App.ViewModels;

/// <summary>What the "Install everything" panel shows.</summary>
public enum SetupPanelState
{
    /// <summary>Nothing is missing (or the setup is not available): no panel.</summary>
    Hidden,

    /// <summary>Something is missing: the "Install everything" button.</summary>
    Offer,

    /// <summary>The consent panel: what will be downloaded, from where, how much, and where it goes.</summary>
    Consent,

    /// <summary>The steps are running.</summary>
    Running,

    /// <summary>A step failed: what failed, Try again, the log.</summary>
    Failed,

    /// <summary>Everything is installed and checked.</summary>
    Succeeded,
}

/// <summary>One line of the setup's step list.</summary>
public sealed record SetupStepItem(SetupStepId Id, string Title, SetupStepState State)
{
    /// <summary>The state in words (also what a screen reader announces after the title).</summary>
    public string StateText => State switch
    {
        SetupStepState.Running => "in progress",
        SetupStepState.Done => "done",
        SetupStepState.Skipped => "already in place",
        SetupStepState.Failed => "failed",
        _ => "waiting",
    };

    public string AutomationName => $"{Title}: {StateText}";
}

/// <summary>One line of the consent panel: what is downloaded, from where, about how much, and under which licence.</summary>
public sealed record SetupDownloadItem(string What, string Host, string Size, string Licence)
{
    public string Details => $"{Host} · {Size} · {Licence}";

    public string AutomationName => $"{What}, from {Host}, {Size}, licence {Licence}";
}

/// <summary>
/// The one-click setup on the Overview page ("Install everything"): offered when something needed for Chinese translation is missing,
/// started only after the user has read what will be downloaded and confirmed. Nothing here runs at start-up or on its own.
/// </summary>
/// <remarks>
/// States: <see cref="SetupPanelState.Hidden"/> / <see cref="SetupPanelState.Offer"/> (follows <see cref="SetNeeded"/>) →
/// <see cref="SetupPanelState.Consent"/> → <see cref="SetupPanelState.Running"/> → <see cref="SetupPanelState.Succeeded"/> or
/// <see cref="SetupPanelState.Failed"/> (Try again resumes: finished steps are skipped). Cancelling returns to the offer.
/// </remarks>
public sealed class SetupViewModel : ViewModelBase
{
    private const double MegaByte = 1024 * 1024;

    private readonly IEnvironmentSetup _setup;
    private readonly SynchronizationContext? _ui;
    private readonly Action<string>? _showLog;
    private readonly ObservableCollection<SetupStepItem> _steps = [];
    private readonly ObservableCollection<SetupDownloadItem> _downloads = [];
    private SetupPanelState _state = SetupPanelState.Hidden;
    private bool _needed;
    private bool _includeTraditional = true;
    private SetupPlan? _plan;
    private string? _planProblem;
    private string _message = string.Empty;
    private string? _notice;
    private string? _errorMessage;
    private string? _failedStep;
    private string _resultText = string.Empty;
    private double _progressPercent;
    private bool _isProgressIndeterminate = true;
    private string _progressText = string.Empty;
    private CancellationTokenSource? _cts;

    /// <param name="setup">Runs the steps.</param>
    /// <param name="uiContext">Where state changes are applied; <see langword="null"/> means inline.</param>
    /// <param name="showLog">Shows the log file (Explorer); without it the path is only displayed.</param>
    public SetupViewModel(IEnvironmentSetup setup, SynchronizationContext? uiContext, Action<string>? showLog = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _ui = uiContext;
        _showLog = showLog;
        Steps = new ReadOnlyObservableCollection<SetupStepItem>(_steps);
        Downloads = new ReadOnlyObservableCollection<SetupDownloadItem>(_downloads);

        ShowConsentCommand = new DelegateCommand(ShowConsent, () => _state == SetupPanelState.Offer);
        CancelConsentCommand = new DelegateCommand(BackToOffer, () => _state == SetupPanelState.Consent);
        StartCommand = new AsyncRelayCommand(RunAsync, () => _state == SetupPanelState.Consent && _plan is not null, uiContext);
        CancelCommand = new DelegateCommand(() => _cts?.Cancel(), () => _state == SetupPanelState.Running && _cts is { IsCancellationRequested: false });
        RetryCommand = new AsyncRelayCommand(RunAsync, () => _state == SetupPanelState.Failed && _plan is not null, uiContext);
        CloseCommand = new DelegateCommand(BackToOffer, () => _state is SetupPanelState.Failed or SetupPanelState.Succeeded);
        ShowLogCommand = new DelegateCommand(() => _showLog?.Invoke(LogPath), () => _showLog is not null && LogPath.Length > 0);
        RebuildPlan();
    }

    /// <summary>Raised on the UI context after a run succeeded, so the readiness checklist is checked again.</summary>
    public event EventHandler? Completed;

    public SetupPanelState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(IsOffer));
            OnPropertyChanged(nameof(IsConsent));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsSucceeded));
            OnPropertyChanged(nameof(ShowsSteps));
            RaiseCommands();
        }
    }

    public bool IsVisible => _state != SetupPanelState.Hidden;

    public bool IsOffer => _state == SetupPanelState.Offer;

    public bool IsConsent => _state == SetupPanelState.Consent;

    public bool IsRunning => _state == SetupPanelState.Running;

    public bool IsFailed => _state == SetupPanelState.Failed;

    public bool IsSucceeded => _state == SetupPanelState.Succeeded;

    /// <summary>Whether the step list is shown (while running and after a run).</summary>
    public bool ShowsSteps => _state is SetupPanelState.Running or SetupPanelState.Failed or SetupPanelState.Succeeded;

    /// <summary>Whether something needed for Chinese translation is missing (set by the readiness checklist).</summary>
    public bool IsNeeded => _needed;

    /// <summary>Also install the Traditional Chinese model (on by default).</summary>
    public bool IncludeTraditional
    {
        get => _includeTraditional;
        set
        {
            if (_state is SetupPanelState.Running || !SetProperty(ref _includeTraditional, value))
            {
                return;
            }

            RebuildPlan();
        }
    }

    public string TraditionalLabel { get; } = string.Create(CultureInfo.InvariantCulture, $"Also install Traditional Chinese (+{SetupManifest.TraditionalChineseModel.Size / MegaByte:0} MB)");

    /// <summary>The consent panel's list of downloads.</summary>
    public ReadOnlyObservableCollection<SetupDownloadItem> Downloads { get; }

    /// <summary>The total, in words.</summary>
    public string TotalText => _plan is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"About {_plan.ApproximateDownloadBytes / MegaByte:0} MB in total.");

    /// <summary>Where things are installed, in words.</summary>
    public string FoldersText => _plan is null
        ? string.Empty
        : $"Python and the packages go to {_plan.RuntimeFolder}, the models to {_plan.ModelsFolder}. Nothing else on this PC is changed and no administrator rights are needed.";

    public string ConsentNote { get; } = "Needs an internet connection. Nothing from your screen is sent. You can cancel at any time.";

    public string LicenceNote { get; } =
        "Licences: the Argos translation models are CC-BY 4.0, the PP-OCRv5 models Apache-2.0, uv MIT or Apache-2.0, Python the PSF licence; "
        + "the packages keep their own open-source licences. They are downloaded from their publishers, not from GlyphLens.";

    /// <summary>Why the setup cannot run here (for example an invalid <c>GLYPHLENS_RUNTIME_DIR</c>), or <see langword="null"/>.</summary>
    public string? PlanProblem
    {
        get => _planProblem;
        private set
        {
            if (SetProperty(ref _planProblem, value))
            {
                OnPropertyChanged(nameof(HasPlanProblem));
            }
        }
    }

    public bool HasPlanProblem => !string.IsNullOrEmpty(_planProblem);

    public ReadOnlyObservableCollection<SetupStepItem> Steps { get; }

    /// <summary>What is happening now, in one short line.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>A note shown with the offer, for example after a cancelled run.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>The step that failed, in words.</summary>
    public string? FailedStep
    {
        get => _failedStep;
        private set => SetProperty(ref _failedStep, value);
    }

    /// <summary>The outcome after a successful run, including warnings.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>"12 MB of 74 MB" while a download runs, otherwise empty.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>The technical log file (installer output only, never screen content).</summary>
    public string LogPath => _plan?.LogFile ?? string.Empty;

    public DelegateCommand ShowConsentCommand { get; }

    public DelegateCommand CancelConsentCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public DelegateCommand CloseCommand { get; }

    public DelegateCommand ShowLogCommand { get; }

    /// <summary>Tells the panel whether something needed for Chinese translation is missing.</summary>
    public void SetNeeded(bool needed)
    {
        if (_needed == needed)
        {
            return;
        }

        _needed = needed;
        OnPropertyChanged(nameof(IsNeeded));
        if (_state == SetupPanelState.Hidden && needed)
        {
            State = SetupPanelState.Offer;
        }
        else if (_state == SetupPanelState.Offer && !needed)
        {
            State = SetupPanelState.Hidden;
            Notice = null;
        }
    }

    /// <summary>Title of a step, in the words of the step list.</summary>
    public static string TitleOf(SetupStepId step) => step switch
    {
        SetupStepId.Prepare => "Check folders and free space",
        SetupStepId.Uv => $"Get uv {SetupManifest.UvVersion} (installs Python)",
        SetupStepId.Python => "Install Python 3.12 for GlyphLens",
        SetupStepId.Packages => "Install the OCR and translation packages",
        SetupStepId.OcrModels => "Download the Chinese OCR models",
        SetupStepId.Worker => "Restart the worker",
        SetupStepId.SimplifiedModel => "Install the Simplified Chinese model",
        SetupStepId.TraditionalModel => "Install the Traditional Chinese model",
        SetupStepId.Check => "Check that it works",
        _ => step.ToString(),
    };

    private void ShowConsent()
    {
        RebuildPlan();
        Notice = null;
        State = SetupPanelState.Consent;
    }

    private void BackToOffer()
    {
        State = _needed ? SetupPanelState.Offer : SetupPanelState.Hidden;
    }

    private void RebuildPlan()
    {
        try
        {
            _plan = _setup.Plan(new SetupOptions(_includeTraditional));
            PlanProblem = null;
        }
        catch (SetupException ex)
        {
            _plan = null;
            PlanProblem = ex.Message;
        }

        _downloads.Clear();
        if (_plan is { } plan)
        {
            foreach (var item in DescribeDownloads(plan))
            {
                _downloads.Add(item);
            }
        }

        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(FoldersText));
        OnPropertyChanged(nameof(LogPath));
        RaiseCommands();
    }

    private static IEnumerable<SetupDownloadItem> DescribeDownloads(SetupPlan plan)
    {
        var uv = plan.Files.FirstOrDefault(f => f.Id == SetupManifest.Uv.Id);
        if (uv is not null)
        {
            yield return new SetupDownloadItem($"uv {SetupManifest.UvVersion}, the Python installer by Astral", uv.Host, Size(uv.Size), uv.Licence);
        }

        yield return new SetupDownloadItem("Python 3.12 (a standard CPython build, fetched by uv)", "releases.astral.sh", Size(SetupManifest.ApproximatePythonDownloadBytes), "PSF licence");
        yield return new SetupDownloadItem(
            string.Create(CultureInfo.InvariantCulture, $"{plan.PackageCount} Python packages for OCR and translation, each checked against a pinned hash"),
            "pypi.org",
            "about " + Size(SetupManifest.ApproximatePackageDownloadBytes),
            "open-source, per package");

        var ocr = plan.Files.Where(f => SetupManifest.OcrFiles.Any(o => o.File.Id == f.Id)).ToList();
        if (ocr.Count > 0)
        {
            yield return new SetupDownloadItem("PP-OCRv5 Chinese OCR models", ocr[0].Host, Size(ocr.Sum(f => f.Size)), ocr[0].Licence);
        }

        foreach (var model in plan.Files.Where(f => f.Id == SetupManifest.SimplifiedChineseModel.Id || f.Id == SetupManifest.TraditionalChineseModel.Id))
        {
            string what = model.Id == SetupManifest.SimplifiedChineseModel.Id ? "Simplified Chinese → English model" : "Traditional Chinese → English model";
            yield return new SetupDownloadItem(what, model.Host, Size(model.Size), model.Licence);
        }
    }

    private static string Size(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(bytes / MegaByte)):0} MB");

    private async Task RunAsync()
    {
        if (_plan is not { } plan)
        {
            return;
        }

        var options = new SetupOptions(_includeTraditional);
        _steps.Clear();
        foreach (var step in plan.Steps)
        {
            _steps.Add(new SetupStepItem(step, TitleOf(step), SetupStepState.Pending));
        }

        ErrorMessage = null;
        FailedStep = null;
        Notice = null;
        ResultText = string.Empty;
        Message = "Starting…";
        SetProgress(null, null);
        using var cts = new CancellationTokenSource();
        _cts = cts;
        State = SetupPanelState.Running;
        bool succeeded = false;
        try
        {
            var result = await _setup.RunAsync(options, new UiProgress(this), cts.Token).ConfigureAwait(true);
            for (int i = 0; i < _steps.Count; i++)
            {
                // Every step completed; the result says which ones were already in place.
                _steps[i] = _steps[i] with { State = result.Skipped.Contains(_steps[i].Id) ? SetupStepState.Skipped : SetupStepState.Done };
            }

            ResultText = result.Warnings.Count == 0
                ? "Everything is installed and checked. From now on GlyphLens works offline."
                : "Installed and checked, with a note: " + string.Join(" ", result.Warnings);
            Message = string.Empty;
            State = SetupPanelState.Succeeded;
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            MarkRunning(SetupStepState.Pending);
            Message = string.Empty;
            State = _needed ? SetupPanelState.Offer : SetupPanelState.Hidden;
            Notice = "Cancelled. Unfinished downloads were deleted; finished steps are kept and reused next time.";
        }
        catch (SetupException ex)
        {
            Fail(ex.Step, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(_steps.FirstOrDefault(s => s.State == SetupStepState.Running)?.Id ?? SetupStepId.Prepare, $"The setup stopped unexpectedly ({ex.GetType().Name}).");
        }
        finally
        {
            _cts = null;
            SetProgress(null, null);
            RaiseCommands();
        }

        if (succeeded)
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Fail(SetupStepId step, string message)
    {
        int index = IndexOf(step);
        if (index >= 0)
        {
            _steps[index] = _steps[index] with { State = SetupStepState.Failed };
        }

        FailedStep = TitleOf(step);
        ErrorMessage = message;
        Message = string.Empty;
        State = SetupPanelState.Failed;
    }

    private void Apply(SetupProgress progress)
    {
        if (_state != SetupPanelState.Running)
        {
            return;
        }

        int index = IndexOf(progress.Step);
        if (index >= 0 && _steps[index].State != progress.State)
        {
            _steps[index] = _steps[index] with { State = progress.State };
        }

        if (progress.State == SetupStepState.Running)
        {
            Message = progress.Message;
            SetProgress(progress.BytesDone, progress.BytesTotal);
        }
        else
        {
            SetProgress(null, null);
        }
    }

    private void SetProgress(long? done, long? total)
    {
        if (done is { } d && total is > 0 and { } t)
        {
            IsProgressIndeterminate = false;
            ProgressPercent = Math.Clamp(100.0 * d / t, 0, 100);
            ProgressText = string.Create(CultureInfo.InvariantCulture, $"{d / MegaByte:0.0} MB of {t / MegaByte:0.0} MB");
        }
        else
        {
            IsProgressIndeterminate = true;
            ProgressPercent = 0;
            ProgressText = string.Empty;
        }
    }

    private void MarkRunning(SetupStepState state)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].State == SetupStepState.Running)
            {
                _steps[i] = _steps[i] with { State = state };
            }
        }
    }

    private int IndexOf(SetupStepId step)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].Id == step)
            {
                return i;
            }
        }

        return -1;
    }

    private void RaiseCommands()
    {
        ShowConsentCommand.RaiseCanExecuteChanged();
        CancelConsentCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        ShowLogCommand.RaiseCanExecuteChanged();
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    /// <summary>Applies the installer's reports on the UI context, in order.</summary>
    private sealed class UiProgress(SetupViewModel owner) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => owner.Post(() => owner.Apply(value));
    }
}
