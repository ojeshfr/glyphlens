using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.ViewModels;

/// <summary>How a translation row is presented.</summary>
public enum TranslationItemKind
{
    /// <summary>Waiting for the translation of this text.</summary>
    Pending,

    /// <summary>Translated.</summary>
    Translated,

    /// <summary>Nothing to translate (already the target language, or no letters).</summary>
    NotNeeded,

    /// <summary>Could not be translated; <see cref="TranslationItem.Note"/> says why.</summary>
    Problem,
}

/// <summary>One recognised line with its translation (or the reason it has none).</summary>
public sealed record TranslationItem(
    string Original,
    string? Translated,
    TranslationItemKind Kind,
    string Note,
    string Language,
    string BoundsText)
{
    public bool HasTranslation => Translated is not null;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool IsProblem => Kind == TranslationItemKind.Problem;

    public bool IsPending => Kind == TranslationItemKind.Pending;

    /// <summary>The text an accessibility client reads for the row.</summary>
    public string AutomationName => Translated is null ? Original : $"{Original} → {Translated}";
}

/// <summary>A language the user can pick. <see cref="Code"/> is <see langword="null"/> for "detect automatically".</summary>
public sealed record TranslationLanguageOption(string? Code, string Label)
{
    /// <summary>The label: what a screen reader announces and what UI Automation reports as the item's name.</summary>
    public override string ToString() => Label;
}

/// <summary>An installed translation package.</summary>
public sealed record ModelPackageItem(string Direction, string Version, string PackageName);

/// <summary>A message about the state of translation (worker down, model missing, ...).</summary>
public sealed record TranslationNotice(string Text, bool IsError, bool IsInfo = false);

/// <summary>
/// State and commands of the translation panel: turn translation on, choose the source and target language, see each
/// recognised line with its translation, and manage the installed models.
/// </summary>
/// <remarks>
/// <para>
/// Translation follows text recognition: every recognition pass (one-off or Live) is translated by the
/// <see cref="TranslationPipeline"/>. Only the latest pass matters: a newer pass cancels the older one, so there is never a
/// backlog, and unchanged lines are answered from the pipeline's caches without contacting the worker. Rows whose text is
/// already known keep their translation while the new pass runs, so a Live update only changes the rows that changed.
/// </para>
/// <para>
/// All work happens off the UI thread (the worker does the heavy lifting in another process); view-model state changes
/// are applied on the UI context. Text is never logged or persisted, and caches are cleared when capture stops. Nothing is
/// downloaded: models are added only from a file the user chooses.
/// </para>
/// </remarks>
public sealed class TranslationViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] CommonLanguages =
    [
        "ar", "zh", "cs", "nl", "en", "fr", "de", "he", "hi", "id", "it", "ja", "ko", "pl", "pt", "ru", "es", "sv", "tr", "uk", "vi",
    ];

    private readonly TranslationPipeline _pipeline;
    private readonly IModelCatalog _catalog;
    private readonly IWorkerProcessManager _worker;
    private readonly IRecognisedTextSource _source;
    private readonly IModelFilePicker? _picker;
    private readonly Action<string>? _copyToClipboard;
    private readonly SynchronizationContext? _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<TranslationItem> _items = [];
    private readonly ObservableCollection<TranslationNotice> _notices = [];
    private List<TranslationNotice> _passNotices = [];
    private TranslationNotice? _engineNotice;
    private PassSummary? _lastPass;
    private bool _isCatalogLoaded;
    private readonly ObservableCollection<TranslationLanguageOption> _sourceOptions = [];
    private readonly ObservableCollection<TranslationLanguageOption> _targetOptions = [];
    private readonly ObservableCollection<ModelPackageItem> _packages = [];

    private IReadOnlyList<ScreenTextRegion> _regions = [];
    private CancellationTokenSource? _passCts;
    private Task _passTask = Task.CompletedTask;
    private long _passVersion;
    private int _busy;
    private int _modelsBusy;
    private bool _isEnabled;
    private bool _isPaused;
    private TranslationLanguageOption _selectedSource;
    private TranslationLanguageOption _selectedTarget;
    private string _statusText = IdleStatus;
    private string _modelsSummary = "Start the worker to see the installed models.";
    private string? _storagePath;
    private string? _modelsError;
    private string? _modelsMessage;
    private HashSet<string> _installedSources = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private const string IdleStatus = "Turn on translation, then read the screen text.";

    public TranslationViewModel(
        TranslationPipeline pipeline,
        IModelCatalog catalog,
        IWorkerProcessManager worker,
        IRecognisedTextSource source,
        IModelFilePicker? picker = null,
        Action<string>? copyToClipboard = null)
        : this(pipeline, catalog, worker, source, picker, copyToClipboard, SynchronizationContext.Current)
    {
    }

    /// <param name="pipeline">Translates recognised regions.</param>
    /// <param name="catalog">The installed models and the model import operation.</param>
    /// <param name="worker">The worker lifecycle (translation starts it when the user turns translation on).</param>
    /// <param name="source">Where recognised regions come from.</param>
    /// <param name="picker">Lets the user choose a model file; without one, importing is unavailable.</param>
    /// <param name="copyToClipboard">Called with the text when the user chooses a copy command.</param>
    /// <param name="uiContext">Where callbacks are delivered; <see langword="null"/> means inline (no ambient fallback).</param>
    public TranslationViewModel(
        TranslationPipeline pipeline,
        IModelCatalog catalog,
        IWorkerProcessManager worker,
        IRecognisedTextSource source,
        IModelFilePicker? picker,
        Action<string>? copyToClipboard,
        SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(source);

        _pipeline = pipeline;
        _catalog = catalog;
        _worker = worker;
        _source = source;
        _picker = picker;
        _copyToClipboard = copyToClipboard;
        _ui = uiContext;

        Items = new ReadOnlyObservableCollection<TranslationItem>(_items);
        Notices = new ReadOnlyObservableCollection<TranslationNotice>(_notices);
        SourceOptions = new ReadOnlyObservableCollection<TranslationLanguageOption>(_sourceOptions);
        TargetOptions = new ReadOnlyObservableCollection<TranslationLanguageOption>(_targetOptions);
        Packages = new ReadOnlyObservableCollection<ModelPackageItem>(_packages);

        _selectedSource = new TranslationLanguageOption(null, "Detect automatically");
        _selectedTarget = new TranslationLanguageOption(LanguageCode.English, LanguageNames.Display(LanguageCode.English));
        RebuildLanguageOptions();

        CopyOriginalCommand = new AsyncRelayCommand(() => CopyAsync(OriginalText, "original"), () => CanCopyOriginal, uiContext);
        CopyTranslationCommand = new AsyncRelayCommand(() => CopyAsync(TranslatedText, "translated"), () => CanCopyTranslation, uiContext);
        StartWorkerCommand = new AsyncRelayCommand(StartWorkerAsync, () => CanStartWorker, uiContext);
        ImportModelCommand = new AsyncRelayCommand(ImportModelAsync, () => CanImportModel, uiContext);
        RefreshModelsCommand = new AsyncRelayCommand(() => RefreshModelsAsync(_lifetime.Token), () => CanRefreshModels, uiContext);

        _source.RegionsRecognised += OnRegionsRecognised;
        _worker.StatusChanged += OnWorkerStatusChanged;
    }

    /// <summary>Raised after each translation pass with its results (one per line or group of lines, in reading order).</summary>
    public event EventHandler<TranslationsPublishedEventArgs>? TranslationsPublished;

    public ReadOnlyObservableCollection<TranslationItem> Items { get; }

    public ReadOnlyObservableCollection<TranslationNotice> Notices { get; }

    public ReadOnlyObservableCollection<TranslationLanguageOption> SourceOptions { get; }

    public ReadOnlyObservableCollection<TranslationLanguageOption> TargetOptions { get; }

    public ReadOnlyObservableCollection<ModelPackageItem> Packages { get; }

    public AsyncRelayCommand CopyOriginalCommand { get; }

    public AsyncRelayCommand CopyTranslationCommand { get; }

    public AsyncRelayCommand StartWorkerCommand { get; }

    public AsyncRelayCommand ImportModelCommand { get; }

    public AsyncRelayCommand RefreshModelsCommand { get; }

    /// <summary>Whether recognised text is translated. Turning it on starts the worker if it is not running.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            if (value)
            {
                StatusText = _regions.Count == 0 ? "Translation is on. Read the screen text to translate it." : "Translating…";
                _ = EnsureWorkerAsync();
            }

            ScheduleTranslation(reuseKnown: false);
            Refresh();
        }
    }

    /// <summary>
    /// While paused, new recognition results are ignored (what is shown stays). Resuming translates the latest recognised text.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!SetProperty(ref _isPaused, value))
            {
                return;
            }

            if (!value)
            {
                ScheduleTranslation(reuseKnown: true);
            }

            Refresh();
        }
    }

    public TranslationLanguageOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || !SetProperty(ref _selectedSource, value))
            {
                return;
            }

            ScheduleTranslation(reuseKnown: false);
        }
    }

    public TranslationLanguageOption SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || !SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            ScheduleTranslation(reuseKnown: false);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasNotices => _notices.Count > 0;

    /// <summary>What the latest translation pass found (counts only), or <see langword="null"/> before the first pass and after the results were discarded.</summary>
    public PassSummary? LastPass
    {
        get => _lastPass;
        private set => SetProperty(ref _lastPass, value);
    }

    /// <summary>Whether the installed models were read from the worker (so "not installed" is a fact, not "not asked yet"); false again when the worker stops.</summary>
    public bool IsCatalogLoaded
    {
        get => _isCatalogLoaded;
        private set => SetProperty(ref _isCatalogLoaded, value);
    }

    /// <summary>The source languages of the installed translation models (not the "no model" entries of <see cref="SourceOptions"/>).</summary>
    public IReadOnlyCollection<string> InstalledSources => _installedSources;

    /// <summary>Whether a translation pass is running.</summary>
    public bool IsTranslating => Volatile.Read(ref _busy) > 0;

    /// <summary>The recognised lines shown in the list, one per row.</summary>
    public string OriginalText => string.Join(Environment.NewLine, _items.Select(i => i.Original));

    /// <summary>The translated lines, one per row (lines without a translation are left out).</summary>
    public string TranslatedText => string.Join(Environment.NewLine, _items.Where(i => i.Translated is not null).Select(i => i.Translated));

    public bool CanCopyOriginal => _items.Count > 0;

    public bool CanCopyTranslation => _items.Any(i => i.Translated is not null);

    public WorkerStatus WorkerStatus => _worker.Status;

    public bool CanStartWorker => WorkerStatus is WorkerStatus.Stopped or WorkerStatus.Error or WorkerStatus.Unavailable;

    /// <summary>The worker's own report of its language-identification and translation engines.</summary>
    public string EngineSummaryText => _worker.LastHealth is { } health
        ? $"Language ID: {health.LanguageDetection.ToDisplayString()} · Translation: {health.Translation.ToDisplayString()} · Models: {health.Models.ToDisplayString()}"
        : $"Worker: {WorkerStatus.ToString().ToUpperInvariant()}";

    // ---- Models -----------------------------------------------------------------------------------------

    public string ModelsSummary
    {
        get => _modelsSummary;
        private set => SetProperty(ref _modelsSummary, value);
    }

    public string? StoragePath
    {
        get => _storagePath;
        private set
        {
            if (SetProperty(ref _storagePath, value))
            {
                OnPropertyChanged(nameof(HasStoragePath));
            }
        }
    }

    public bool HasStoragePath => !string.IsNullOrEmpty(_storagePath);

    public string? ModelsError
    {
        get => _modelsError;
        private set
        {
            if (SetProperty(ref _modelsError, value))
            {
                OnPropertyChanged(nameof(HasModelsError));
            }
        }
    }

    public bool HasModelsError => !string.IsNullOrEmpty(_modelsError);

    public string? ModelsMessage
    {
        get => _modelsMessage;
        private set
        {
            if (SetProperty(ref _modelsMessage, value))
            {
                OnPropertyChanged(nameof(HasModelsMessage));
            }
        }
    }

    public bool HasModelsMessage => !string.IsNullOrEmpty(_modelsMessage);

    public bool IsModelsBusy => Volatile.Read(ref _modelsBusy) > 0;

    public bool CanImportModel => _picker is not null && !IsModelsBusy;

    public bool CanRefreshModels => !IsModelsBusy && WorkerStatus == WorkerStatus.Ready;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RegionsRecognised -= OnRegionsRecognised;
        _worker.StatusChanged -= OnWorkerStatusChanged;
        CancelPass();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Waits for the current translation pass to finish (for tests and orderly shutdown).</summary>
    public Task WaitForIdleAsync() => _passTask;

    // ---- Recognition and passes -------------------------------------------------------------------------

    private void OnRegionsRecognised(object? sender, RegionsRecognisedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (_isPaused && !args.Discarded)
        {
            return;
        }

        _regions = args.Regions;
        if (args.Discarded)
        {
            // The results describe a screen that is no longer captured: forget them, including cached text.
            _pipeline.ClearCaches();
        }

        ScheduleTranslation(reuseKnown: true);
    }

    private TranslationSelection CurrentSelection() => new(_selectedSource.Code, _selectedTarget.Code ?? LanguageCode.English);

    private void ScheduleTranslation(bool reuseKnown)
    {
        if (_disposed)
        {
            return;
        }

        CancelPass();
        long version = Volatile.Read(ref _passVersion);

        if (!_isEnabled || _regions.Count == 0)
        {
            ClearItems();
            StatusText = !_isEnabled ? IdleStatus : "No text recognised yet. Read the screen text to translate it.";
            Refresh();
            return;
        }

        var cts = new CancellationTokenSource();
        _passCts = cts;
        ShowPending(_regions, reuseKnown);
        StatusText = "Translating…";
        Interlocked.Increment(ref _busy);
        Refresh();
        _passTask = RunPassAsync(_regions, CurrentSelection(), cts, version);
    }

    private void CancelPass()
    {
        var cts = Interlocked.Exchange(ref _passCts, null);
        Interlocked.Increment(ref _passVersion);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass finished and released its source at the same moment; there is nothing left to cancel.
        }
    }

    private async Task RunPassAsync(IReadOnlyList<ScreenTextRegion> regions, TranslationSelection selection, CancellationTokenSource cts, long version)
    {
        var token = cts.Token;
        try
        {
            var watch = Stopwatch.StartNew();
            var pass = await _pipeline.TranslateAsync(regions, selection, token).ConfigureAwait(true);
            watch.Stop();

            if (!IsCurrent(version))
            {
                return;
            }

            Publish(pass, selection, watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer pass, or stopped.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsCurrent(version))
            {
                // Never include the exception message: it could in principle carry screen text.
                StatusText = $"Translation failed unexpectedly ({ex.GetType().Name}).";
                SetNotices([new TranslationNotice(StatusText, IsError: true)]);
            }
        }
        finally
        {
            // Stop pointing at the source before releasing it, so a later cancel never touches a disposed one.
            _ = Interlocked.CompareExchange(ref _passCts, null, cts);
            Interlocked.Decrement(ref _busy);
            cts.Dispose();
            Refresh();
        }
    }

    private bool IsCurrent(long version) => !_disposed && Volatile.Read(ref _passVersion) == version;

    private void ShowPending(IReadOnlyList<ScreenTextRegion> regions, bool reuseKnown)
    {
        var known = new Dictionary<string, TranslationItem>(StringComparer.Ordinal);
        if (reuseKnown)
        {
            foreach (var item in _items)
            {
                if (!item.IsPending)
                {
                    known.TryAdd(TextNormalizer.Normalize(item.Original), item);
                }
            }
        }

        var rows = new List<TranslationItem>(regions.Count);
        foreach (var region in regions)
        {
            string bounds = BoundsOf(region);
            rows.Add(
                known.TryGetValue(TextNormalizer.Normalize(region.Text), out var previous)
                    ? previous with { Original = region.Text, BoundsText = bounds }
                    : new TranslationItem(region.Text, null, TranslationItemKind.Pending, "Translating…", string.Empty, bounds));
        }

        Reconcile(rows);
    }

    private void Publish(TranslationPassResult pass, TranslationSelection selection, TimeSpan elapsed)
    {
        var rows = pass.Regions.Select(ToItem).ToList();
        Reconcile(rows);
        SetNotices(BuildNotices(pass.Regions, selection));
        LastPass = PassSummary.From(pass.Regions);
        TranslationsPublished?.Invoke(this, new TranslationsPublishedEventArgs(pass.Regions, DateTimeOffset.UtcNow));

        int translated = pass.Regions.Count(r => r.IsTranslated);
        int attention = pass.Regions.Count(r => r.Status is not (RegionTranslationStatus.Translated or RegionTranslationStatus.AlreadyTarget or RegionTranslationStatus.NoText));
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{translated} of {pass.Regions.Count} translated · {pass.CacheHits} cached · {elapsed.TotalMilliseconds:0} ms{(attention > 0 ? $" · {attention} need attention" : string.Empty)}");
    }

    private TranslationItem ToItem(RegionTranslation r)
    {
        string bounds = BoundsOf(r.Region);
        string language = DescribeLanguage(r);
        return r.Status switch
        {
            RegionTranslationStatus.Translated => new TranslationItem(r.Region.Text, r.TranslatedText, TranslationItemKind.Translated, string.Empty, language, bounds),
            RegionTranslationStatus.AlreadyTarget or RegionTranslationStatus.NoText =>
                new TranslationItem(r.Region.Text, null, TranslationItemKind.NotNeeded, r.Message ?? "Nothing to translate.", language, bounds),
            _ => new TranslationItem(r.Region.Text, null, TranslationItemKind.Problem, r.Message ?? "Could not be translated.", language, bounds),
        };
    }

    private static string DescribeLanguage(RegionTranslation r)
    {
        if (r.SourceLanguage is null)
        {
            return r.Alternatives.Count > 0 ? $"Unsure · maybe {LanguageNames.Display(r.Alternatives[0].Language)}" : "Language unknown";
        }

        string name = LanguageNames.Display(r.SourceLanguage);
        string detail = r.SourceChosenByUser
            ? " (chosen)"
            : r.DetectionConfidence is { } c ? string.Create(CultureInfo.InvariantCulture, $" · {c * 100:0}%") : string.Empty;

        // Whether the answer came from the cache is deliberately not part of the row: it would make an unchanged line
        // look "changed" on the next pass. The status line reports the cache hits.
        return name + detail;
    }

    private static string BoundsOf(ScreenTextRegion region) =>
        string.Create(CultureInfo.InvariantCulture, $"{region.X},{region.Y}  {region.Width}×{region.Height}");

    private List<TranslationNotice> BuildNotices(IReadOnlyList<RegionTranslation> regions, TranslationSelection selection)
    {
        var notices = new List<TranslationNotice>();

        if (regions.Any(r => r.Status == RegionTranslationStatus.WorkerUnavailable))
        {
            string detail = _worker.LastError is { Length: > 0 } error ? $" ({error})" : string.Empty;
            notices.Add(new TranslationNotice($"The local translation worker is not running{detail}. Start it to translate.", IsError: true));
        }

        foreach (var message in regions.Where(r => r.Status == RegionTranslationStatus.EngineUnavailable).Select(r => r.Message).Distinct().Take(1))
        {
            notices.Add(new TranslationNotice(message ?? "The local translation engine is not available.", IsError: true));
        }

        foreach (var missing in regions.Where(r => r.Status == RegionTranslationStatus.ModelMissing).Select(r => r.Message).Where(m => m is not null).Distinct().Take(3))
        {
            notices.Add(new TranslationNotice(missing!, IsError: false));
        }

        int uncertain = regions.Count(r => r.Status == RegionTranslationStatus.LanguageUncertain);
        if (uncertain > 0)
        {
            notices.Add(new TranslationNotice(
                string.Create(CultureInfo.InvariantCulture, $"{uncertain} line{(uncertain == 1 ? " has" : "s have")} an uncertain language. Choose the source language above to translate {(uncertain == 1 ? "it" : "them")}."),
                IsError: false));
        }

        foreach (var message in regions.Where(r => r.Status == RegionTranslationStatus.UnsupportedLanguage).Select(r => r.Message).Where(m => m is not null).Distinct().Take(2))
        {
            notices.Add(new TranslationNotice(message!, IsError: false));
        }

        int failed = regions.Count(r => r.Status == RegionTranslationStatus.Failed);
        if (failed > 0)
        {
            notices.Add(new TranslationNotice(
                string.Create(CultureInfo.InvariantCulture, $"{failed} line{(failed == 1 ? string.Empty : "s")} could not be translated."),
                IsError: true));
        }

        if (selection.SourceLanguage is null && regions.Count > 0 && regions.All(r => r.Status is RegionTranslationStatus.NoText))
        {
            notices.Clear();
        }

        return notices;
    }

    /// <summary>
    /// Sets (or, with <see langword="null"/>, clears) a notice about the text-recognition engine, for example that Chinese cannot be
    /// read yet. It is listed first among the notices, next to those of the latest translation pass, and survives clearing the results.
    /// </summary>
    public void SetEngineNotice(string? text, bool isError, bool isInfo = false)
    {
        var notice = string.IsNullOrEmpty(text) ? null : new TranslationNotice(text, isError, isInfo);
        if (Equals(_engineNotice, notice))
        {
            return;
        }

        _engineNotice = notice;
        SetNotices(_passNotices);
    }

    private void SetNotices(List<TranslationNotice> passNotices)
    {
        _passNotices = passNotices;
        var notices = _engineNotice is null ? passNotices : [_engineNotice, .. passNotices];
        if (_notices.Count == notices.Count && _notices.Zip(notices).All(p => p.First == p.Second))
        {
            return;
        }

        _notices.Clear();
        foreach (var notice in notices)
        {
            _notices.Add(notice);
        }

        OnPropertyChanged(nameof(HasNotices));
    }

    private void ClearItems()
    {
        LastPass = null;
        _items.Clear();
        SetNotices([]);
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(TranslatedText));
    }

    /// <summary>Makes the list equal to <paramref name="rows"/> while touching only the rows that differ.</summary>
    private void Reconcile(List<TranslationItem> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= _items.Count)
            {
                _items.Add(rows[i]);
            }
            else if (_items[i] != rows[i])
            {
                _items[i] = rows[i];
            }
        }

        while (_items.Count > rows.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(TranslatedText));
        Refresh();
    }

    // ---- Worker and models ------------------------------------------------------------------------------

    private async Task EnsureWorkerAsync()
    {
        try
        {
            if (CanStartWorker)
            {
                await _worker.StartAsync(_lifetime.Token).ConfigureAwait(true);
            }
            else if (WorkerStatus == WorkerStatus.Ready && _packages.Count == 0 && !IsModelsBusy)
            {
                await RefreshModelsAsync(_lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the application.
        }
        catch (WorkerException ex)
        {
            ModelsError = ex.Message;
        }
    }

    private async Task StartWorkerAsync()
    {
        ModelsError = null;
        try
        {
            await _worker.StartAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Closing the application.
        }
        catch (WorkerException ex)
        {
            ModelsError = ex.Message;
        }
    }

    private async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        if (WorkerStatus != WorkerStatus.Ready)
        {
            ModelsSummary = "Start the worker to see the installed models.";
            return;
        }

        Interlocked.Increment(ref _modelsBusy);
        ModelsError = null;
        Refresh();
        try
        {
            _pipeline.InvalidateRoutes();
            var catalog = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(true);
            ApplyCatalog(catalog);
            try
            {
                _ = await _worker.GetHealthAsync(cancellationToken).ConfigureAwait(true); // brings the engine states up to date
            }
            catch (WorkerException)
            {
                // The panel still shows the catalog; the health rows simply keep their last values.
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the application.
        }
        catch (WorkerException ex)
        {
            ModelsSummary = "Could not read the installed models.";
            ModelsError = ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _modelsBusy);
            Refresh();
        }
    }

    private void ApplyCatalog(ModelCatalog catalog)
    {
        IsCatalogLoaded = true;
        _packages.Clear();
        foreach (var package in catalog.Packages.OrderBy(p => p.From, StringComparer.Ordinal).ThenBy(p => p.To, StringComparer.Ordinal))
        {
            _packages.Add(new ModelPackageItem(
                $"{LanguageNames.Display(package.From)} → {LanguageNames.Display(package.To)}",
                package.Version,
                package.PackageName));
        }

        StoragePath = catalog.StoragePath;
        _installedSources = new HashSet<string>(catalog.InstalledSources, StringComparer.OrdinalIgnoreCase);
        ModelsSummary = catalog.Packages.Count == 0
            ? "No translation models installed. Import an Argos .argosmodel file to translate."
            : string.Create(CultureInfo.InvariantCulture, $"{catalog.Packages.Count} model{(catalog.Packages.Count == 1 ? string.Empty : "s")} installed.");
        ModelsError = catalog.Problems.Count > 0 ? string.Join(" ", catalog.Problems) : null;
        RebuildLanguageOptions(catalog);
    }

    private void RebuildLanguageOptions(ModelCatalog? catalog = null)
    {
        string? selectedSource = _selectedSource.Code;
        string selectedTarget = _selectedTarget.Code ?? LanguageCode.English;

        var installedTargets = catalog is null
            ? [LanguageCode.English]
            : catalog.Packages.Select(p => p.To).Append(LanguageCode.English).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sources = new List<TranslationLanguageOption> { new(null, "Detect automatically") };
        foreach (string code in _installedSources.OrderBy(c => LanguageNames.Display(c), StringComparer.Ordinal))
        {
            sources.Add(new TranslationLanguageOption(code, LanguageNames.Display(code)));
        }

        foreach (string code in CommonLanguages.Where(c => !_installedSources.Contains(c)).OrderBy(c => LanguageNames.Display(c), StringComparer.Ordinal))
        {
            sources.Add(new TranslationLanguageOption(code, $"{LanguageNames.Display(code)} (no model)"));
        }

        var targets = installedTargets
            .OrderBy(c => string.Equals(c, LanguageCode.English, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => LanguageNames.Display(c), StringComparer.Ordinal)
            .Select(c => new TranslationLanguageOption(c, LanguageNames.Display(c)))
            .ToList();

        Replace(_sourceOptions, sources);
        Replace(_targetOptions, targets);

        // Keep the user's choice (by code) when the lists change underneath it.
        var source = sources.FirstOrDefault(o => string.Equals(o.Code, selectedSource, StringComparison.OrdinalIgnoreCase)) ?? sources[0];
        var target = targets.FirstOrDefault(o => string.Equals(o.Code, selectedTarget, StringComparison.OrdinalIgnoreCase)) ?? targets[0];
        _selectedSource = source;
        _selectedTarget = target;
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedTarget));
        OnPropertyChanged(nameof(InstalledSources));
    }

    private static void Replace(ObservableCollection<TranslationLanguageOption> collection, List<TranslationLanguageOption> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private async Task ImportModelAsync()
    {
        if (_picker is null)
        {
            return;
        }

        string? path = _picker.PickModelFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ModelsError = null;
        ModelsMessage = null;
        Interlocked.Increment(ref _modelsBusy);
        Refresh();
        try
        {
            if (CanStartWorker)
            {
                await _worker.StartAsync(_lifetime.Token).ConfigureAwait(true);
            }

            var package = await _catalog.ImportPackageAsync(path, _lifetime.Token).ConfigureAwait(true);
            ModelsMessage = $"Installed {LanguageNames.Display(package.From)} → {LanguageNames.Display(package.To)} (version {package.Version}).";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TranslationException ex)
        {
            ModelsError = ex.Error.Message;
            return;
        }
        catch (WorkerException ex)
        {
            ModelsError = ex.Message;
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _modelsBusy);
            Refresh();
        }

        await RefreshModelsAsync(_lifetime.Token).ConfigureAwait(true);
        ScheduleTranslation(reuseKnown: false);
    }

    private void OnWorkerStatusChanged(object? sender, WorkerStatusChangedEventArgs args)
    {
        if (_ui is null)
        {
            ApplyWorkerStatus();
        }
        else
        {
            _ui.Post(_ => ApplyWorkerStatus(), null);
        }
    }

    private void ApplyWorkerStatus()
    {
        if (_disposed)
        {
            return;
        }

        Refresh();
        if (WorkerStatus != WorkerStatus.Ready)
        {
            IsCatalogLoaded = false;
        }

        if (WorkerStatus == WorkerStatus.Ready)
        {
            // The worker (re)started: load the engines and re-translate what is on screen.
            _ = RefreshAndRetranslateAsync();
        }
        else if (WorkerStatus is WorkerStatus.Stopped or WorkerStatus.Error or WorkerStatus.Unavailable)
        {
            ModelsSummary = "Start the worker to see the installed models.";
        }
    }

    private async Task RefreshAndRetranslateAsync()
    {
        await RefreshModelsAsync(_lifetime.Token).ConfigureAwait(true);
        if (_isEnabled && _regions.Count > 0)
        {
            ScheduleTranslation(reuseKnown: false);
        }
    }

    // ---- Clipboard --------------------------------------------------------------------------------------

    private Task CopyAsync(string text, string what)
    {
        if (_copyToClipboard is null || string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        try
        {
            _copyToClipboard(text);
            StatusText = $"Copied the {what} text.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusText = "The clipboard is busy; try again.";
        }

        return Task.CompletedTask;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsTranslating));
        OnPropertyChanged(nameof(WorkerStatus));
        OnPropertyChanged(nameof(CanStartWorker));
        OnPropertyChanged(nameof(EngineSummaryText));
        OnPropertyChanged(nameof(CanCopyOriginal));
        OnPropertyChanged(nameof(CanCopyTranslation));
        OnPropertyChanged(nameof(IsModelsBusy));
        OnPropertyChanged(nameof(CanImportModel));
        OnPropertyChanged(nameof(CanRefreshModels));
        CopyOriginalCommand.RaiseCanExecuteChanged();
        CopyTranslationCommand.RaiseCanExecuteChanged();
        StartWorkerCommand.RaiseCanExecuteChanged();
        ImportModelCommand.RaiseCanExecuteChanged();
        RefreshModelsCommand.RaiseCanExecuteChanged();
    }
}
