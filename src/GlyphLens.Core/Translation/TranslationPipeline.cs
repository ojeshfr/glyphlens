using System.Globalization;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Translation;

/// <summary>
/// Turns recognised screen text into translations: identifies each region's language (or uses the one the user chose), skips
/// text that needs no translation, checks that a local model exists, reuses cached translations, and translates the rest in
/// one batch. Every region keeps its identity and coordinates in the result.
/// </summary>
/// <remarks>
/// <para>
/// Passes are serialised: only one runs at a time, and a pass waiting for its turn honours cancellation, so a caller that
/// cancels the previous pass before starting the next never builds a backlog. Identical texts within one pass are identified
/// and translated once. Nothing is translated by a fallback: when a model is missing or the language is uncertain, the
/// result says so and carries no translated text.
/// </para>
/// <para>
/// Text is private screen content. It is held only in memory (the bounded caches), never logged, and cleared by
/// <see cref="ClearCaches"/>.
/// </para>
/// </remarks>
public sealed class TranslationPipeline : IDisposable
{
    private readonly ILanguageDetector _detector;
    private readonly IModelCatalog _catalog;
    private readonly ITranslationEngine _engine;
    private readonly ITranslationCache _cache;
    private readonly TranslationPipelineOptions _options;
    private static readonly IdentityRegionGrouper IdentityGrouper = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly LruMap<string, LanguageDetectionResult> _detections;
    private readonly Dictionary<(string Source, string Target), RouteStatus> _routes = [];
    private bool _disposed;

    public TranslationPipeline(
        ILanguageDetector detector,
        IModelCatalog catalog,
        ITranslationEngine engine,
        ITranslationCache cache,
        TranslationPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(cache);

        _options = options ?? new TranslationPipelineOptions();
        if (_options.MinimumConfidence is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumConfidence must be between 0 and 1.");
        }

        _detector = detector;
        _catalog = catalog;
        _engine = engine;
        _cache = cache;
        _detections = new LruMap<string, LanguageDetectionResult>(_options.DetectionCacheCapacity, StringComparer.Ordinal);
        _catalog.Changed += OnCatalogChanged;
    }

    /// <summary>Forgets which language pairs are available, so the next pass asks the catalog again (the installed models may have changed).</summary>
    public void InvalidateRoutes()
    {
        lock (_stateLock)
        {
            _routes.Clear();
        }
    }

    /// <summary>Forgets cached detections, routes and translations (they are screen content and model facts).</summary>
    public void ClearCaches()
    {
        lock (_stateLock)
        {
            _detections.Clear();
            _routes.Clear();
        }

        _cache.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalog.Changed -= OnCatalogChanged;
        _gate.Dispose();
    }

    /// <summary>Translates <paramref name="regions"/> according to <paramref name="selection"/>. The result has one entry per region, in order.</summary>
    /// <exception cref="OperationCanceledException">The pass was cancelled (a newer one superseded it, or the user stopped it).</exception>
    public async Task<TranslationPassResult> TranslateAsync(
        IReadOnlyList<ScreenTextRegion> regions,
        TranslationSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(selection);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (regions.Count == 0)
        {
            return new TranslationPassResult([], 0, 0, 0);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var groups = (_options.Grouper ?? IdentityGrouper).Group(regions);
            var pass = await RunAsync([.. groups.Select(g => g.Region)], selection, cancellationToken).ConfigureAwait(false);

            // Every result keeps the lines it covers and its place in reading order.
            var results = new RegionTranslation[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                results[i] = pass.Regions[i] with { Members = groups[i].Members, ReadingOrder = groups[i].ReadingOrder };
            }

            return pass with { Regions = results };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TranslationPassResult> RunAsync(
        IReadOnlyList<ScreenTextRegion> regions,
        TranslationSelection selection,
        CancellationToken cancellationToken)
    {
        // Group regions by normalised text: identical text is identified and translated once.
        var normalized = new string[regions.Count];
        var distinct = new List<string>();
        var plans = new Dictionary<string, TextPlan>(StringComparer.Ordinal);
        for (int i = 0; i < regions.Count; i++)
        {
            string text = TextNormalizer.Normalize(regions[i].Text);
            normalized[i] = text;
            if (plans.TryAdd(text, TextPlan.Pending))
            {
                distinct.Add(text);
            }
        }

        int textsDetected = await ResolveSourcesAsync(distinct, plans, selection, cancellationToken).ConfigureAwait(false);
        int cacheHits = 0;
        int textsTranslated = 0;

        // Language pairs that still need work, with their route status.
        var needing = distinct.Where(t => plans[t].Status == PlanStatus.NeedsRoute).ToList();
        var routes = new Dictionary<string, RouteStatus>(StringComparer.OrdinalIgnoreCase);
        var pairFailures = new Dictionary<string, TextPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in needing.Select(t => plans[t].Source!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                routes[source] = await GetRouteAsync(source, selection.TargetLanguage, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkerException)
            {
                pairFailures[source] = TextPlan.Terminal(
                    RegionTranslationStatus.WorkerUnavailable,
                    "The local translation worker is not running. Start it from the Worker panel.");
            }
        }

        var misses = new List<(string Text, string Source, RouteStatus Route)>();
        foreach (string text in needing)
        {
            var plan = plans[text];
            string source = plan.Source!;
            if (pairFailures.TryGetValue(source, out var failure))
            {
                plans[text] = plan.WithOutcome(failure);
                continue;
            }

            var route = routes[source];
            if (!route.Available)
            {
                plans[text] = plan.WithOutcome(UnavailableRoute(route));
                continue;
            }

            var key = new TranslationCacheKey(text, source, selection.TargetLanguage, route.ModelVersion!);
            if (_cache.TryGet(key, out var cached) && cached is not null)
            {
                cacheHits++;
                plans[text] = plan.WithTranslation(cached.TranslatedText, route.ModelVersion!, fromCache: true);
            }
            else
            {
                misses.Add((text, source, route));
            }
        }

        if (misses.Count > 0)
        {
            textsTranslated = misses.Count;
            await TranslateMissesAsync(misses, plans, selection, cancellationToken).ConfigureAwait(false);
        }

        var results = new RegionTranslation[regions.Count];
        for (int i = 0; i < regions.Count; i++)
        {
            results[i] = plans[normalized[i]].ToRegionTranslation(regions[i]);
        }

        return new TranslationPassResult(results, cacheHits, textsTranslated, textsDetected);
    }

    /// <summary>Decides each distinct text's source language: user choice, cached or fresh detection, or a terminal status. Returns how many texts were sent to the detector.</summary>
    private async Task<int> ResolveSourcesAsync(
        List<string> distinct,
        Dictionary<string, TextPlan> plans,
        TranslationSelection selection,
        CancellationToken cancellationToken)
    {
        var toDetect = new List<string>();
        foreach (string text in distinct)
        {
            if (text.Length == 0 || TextNormalizer.CountLetters(text) == 0)
            {
                plans[text] = TextPlan.Terminal(RegionTranslationStatus.NoText, "Nothing to translate (no letters).");
            }
            else if (selection.SourceLanguage is { } chosen)
            {
                plans[text] = Source(chosen, chosenByUser: true, confidence: null, [], selection.TargetLanguage);
            }
            else if (TryGetDetection(text, out var cached))
            {
                plans[text] = FromDetection(cached, selection.TargetLanguage);
            }
            else
            {
                toDetect.Add(text);
            }
        }

        if (toDetect.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<LanguageDetectionResult> detected;
        try
        {
            detected = await _detector.DetectAsync(toDetect, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerException)
        {
            foreach (string text in toDetect)
            {
                plans[text] = TextPlan.Terminal(
                    RegionTranslationStatus.WorkerUnavailable,
                    "The local translation worker is not running. Start it from the Worker panel.");
            }

            return toDetect.Count;
        }

        if (detected.Count != toDetect.Count)
        {
            foreach (string text in toDetect)
            {
                plans[text] = TextPlan.Terminal(RegionTranslationStatus.Failed, "Language identification returned an unexpected result.");
            }

            return toDetect.Count;
        }

        for (int i = 0; i < toDetect.Count; i++)
        {
            StoreDetection(toDetect[i], detected[i]);
            plans[toDetect[i]] = FromDetection(detected[i], selection.TargetLanguage);
        }

        return toDetect.Count;
    }

    private TextPlan FromDetection(LanguageDetectionResult detection, string target)
    {
        if (detection.Language is not { } language)
        {
            return detection.Issue switch
            {
                LanguageDetectionIssue.NoText => TextPlan.Terminal(RegionTranslationStatus.NoText, "Nothing to translate (no letters)."),
                LanguageDetectionIssue.TooShort => TextPlan.Uncertain("Too short to identify the language. Choose the source language to translate it.", detection.Alternatives),
                _ => TextPlan.Uncertain("The language could not be identified. Choose the source language to translate it.", detection.Alternatives),
            };
        }

        if (detection.Confidence is { } confidence && confidence < _options.MinimumConfidence)
        {
            var guesses = new List<LanguageCandidate> { new(language, confidence) };
            guesses.AddRange(detection.Alternatives);
            string percent = (confidence * 100).ToString("0", CultureInfo.InvariantCulture);
            return TextPlan.Uncertain(
                $"Not sure which language this is (best guess {LanguageNames.Display(language)}, {percent}%). Choose the source language to translate it.",
                guesses);
        }

        return Source(language, chosenByUser: false, detection.Confidence, detection.Alternatives, target);
    }

    private static TextPlan Source(string source, bool chosenByUser, double? confidence, IReadOnlyList<LanguageCandidate> alternatives, string target)
    {
        if (LanguageNames.SamePrimary(source, target))
        {
            return new TextPlan(PlanStatus.Done, RegionTranslationStatus.AlreadyTarget)
            {
                Source = source,
                ChosenByUser = chosenByUser,
                Confidence = confidence,
                Alternatives = alternatives,
                Message = $"Already {LanguageNames.Display(target)}; nothing to translate.",
            };
        }

        return new TextPlan(PlanStatus.NeedsRoute, RegionTranslationStatus.Translated)
        {
            Source = source,
            ChosenByUser = chosenByUser,
            Confidence = confidence,
            Alternatives = alternatives,
        };
    }

    private static TextPlan UnavailableRoute(RouteStatus route)
    {
        string pair = $"{LanguageNames.Display(route.Source)} to {LanguageNames.Display(route.Target)}";
        return route.Issue switch
        {
            RouteIssue.UnsupportedPair => TextPlan.Terminal(RegionTranslationStatus.UnsupportedLanguage, $"Translating {pair} is not supported by the local engine."),
            RouteIssue.EngineUnavailable => TextPlan.Terminal(RegionTranslationStatus.EngineUnavailable, "The local translation engine is not available. See the Models panel."),
            _ => TextPlan.Terminal(
                RegionTranslationStatus.ModelMissing,
                route.MissingPackage is { } package
                    ? $"No installed model translates {pair}. Import the {package} package from the Models panel."
                    : $"No installed model translates {pair}. Import a model package from the Models panel.",
                route.MissingPackage),
        };
    }

    private async Task TranslateMissesAsync(
        List<(string Text, string Source, RouteStatus Route)> misses,
        Dictionary<string, TextPlan> plans,
        TranslationSelection selection,
        CancellationToken cancellationToken)
    {
        var requests = misses.Select(m => new TranslationRequest(m.Text, m.Source, selection.TargetLanguage)).ToList();

        IReadOnlyList<TranslationOutcome> outcomes;
        try
        {
            outcomes = await _engine.TranslateBatchAsync(requests, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            ApplyFailure(misses, plans, FromError(ex.Error));
            return;
        }
        catch (WorkerException)
        {
            ApplyFailure(
                misses,
                plans,
                TextPlan.Terminal(RegionTranslationStatus.WorkerUnavailable, "The local translation worker is not running. Start it from the Worker panel."));
            return;
        }

        if (outcomes.Count != misses.Count)
        {
            ApplyFailure(misses, plans, TextPlan.Terminal(RegionTranslationStatus.Failed, "Translation returned an unexpected result."));
            return;
        }

        for (int i = 0; i < misses.Count; i++)
        {
            var (text, source, route) = misses[i];
            var plan = plans[text];
            if (outcomes[i].Result is { } result)
            {
                _cache.Store(new TranslationCacheKey(text, source, selection.TargetLanguage, route.ModelVersion!), result);
                plans[text] = plan.WithTranslation(result.TranslatedText, route.ModelVersion!, fromCache: false);
            }
            else
            {
                plans[text] = plan.WithOutcome(FromError(outcomes[i].Error!));
            }
        }
    }

    private static void ApplyFailure(List<(string Text, string Source, RouteStatus Route)> misses, Dictionary<string, TextPlan> plans, TextPlan failure)
    {
        foreach (var miss in misses)
        {
            plans[miss.Text] = plans[miss.Text].WithOutcome(failure);
        }
    }

    private static TextPlan FromError(TranslationError error) => error.Kind switch
    {
        TranslationErrorKind.ModelMissing => TextPlan.Terminal(RegionTranslationStatus.ModelMissing, error.Message, error.RequiredPackage),
        TranslationErrorKind.UnsupportedLanguagePair => TextPlan.Terminal(RegionTranslationStatus.UnsupportedLanguage, error.Message),
        TranslationErrorKind.SameLanguage => TextPlan.Terminal(RegionTranslationStatus.AlreadyTarget, error.Message),
        TranslationErrorKind.EmptyText => TextPlan.Terminal(RegionTranslationStatus.NoText, error.Message),
        TranslationErrorKind.LanguageUndetermined => TextPlan.Uncertain(error.Message, []),
        TranslationErrorKind.WorkerUnavailable => TextPlan.Terminal(RegionTranslationStatus.WorkerUnavailable, error.Message),
        TranslationErrorKind.EngineUnavailable => TextPlan.Terminal(RegionTranslationStatus.EngineUnavailable, error.Message),
        _ => TextPlan.Terminal(RegionTranslationStatus.Failed, error.Message),
    };

    private async Task<RouteStatus> GetRouteAsync(string source, string target, CancellationToken cancellationToken)
    {
        var key = (source.ToLowerInvariant(), target.ToLowerInvariant());
        lock (_stateLock)
        {
            if (_routes.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        var status = await _catalog.GetRouteStatusAsync(source, target, cancellationToken).ConfigureAwait(false);

        // Only a settled answer is remembered: "engine unavailable" may be fixed by the next restart.
        if (status.Available || status.Issue != RouteIssue.EngineUnavailable)
        {
            lock (_stateLock)
            {
                _routes[key] = status;
            }
        }

        return status;
    }

    private bool TryGetDetection(string text, out LanguageDetectionResult detection)
    {
        lock (_stateLock)
        {
            if (_detections.TryGet(text, out var found) && found is not null)
            {
                detection = found;
                return true;
            }
        }

        detection = null!;
        return false;
    }

    private void StoreDetection(string text, LanguageDetectionResult detection)
    {
        lock (_stateLock)
        {
            _detections.Set(text, detection);
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        // The installed models may differ now: routes and cached translations are no longer trustworthy.
        lock (_stateLock)
        {
            _routes.Clear();
        }

        _cache.Clear();
    }

    private enum PlanStatus
    {
        Pending,
        NeedsRoute,
        Done,
    }

    private sealed record TextPlan(PlanStatus Status, RegionTranslationStatus Outcome = RegionTranslationStatus.Failed)
    {
        public static TextPlan Pending { get; } = new(PlanStatus.Pending);

        public string? Source { get; init; }

        public bool ChosenByUser { get; init; }

        public double? Confidence { get; init; }

        public IReadOnlyList<LanguageCandidate> Alternatives { get; init; } = [];

        public string? Message { get; init; }

        public string? RequiredPackage { get; init; }

        public string? Translated { get; init; }

        public bool FromCache { get; init; }

        public string? ModelVersion { get; init; }

        public static TextPlan Terminal(RegionTranslationStatus outcome, string message, string? requiredPackage = null) =>
            new(PlanStatus.Done, outcome) { Message = message, RequiredPackage = requiredPackage };

        public static TextPlan Uncertain(string message, IReadOnlyList<LanguageCandidate> alternatives) =>
            new(PlanStatus.Done, RegionTranslationStatus.LanguageUncertain) { Message = message, Alternatives = alternatives };

        /// <summary>Keeps this plan's language facts and takes the outcome of <paramref name="other"/>.</summary>
        public TextPlan WithOutcome(TextPlan other) => this with
        {
            Status = PlanStatus.Done,
            Outcome = other.Outcome,
            Message = other.Message,
            RequiredPackage = other.RequiredPackage,
            Alternatives = other.Alternatives.Count > 0 ? other.Alternatives : Alternatives,
            Translated = null,
        };

        public TextPlan WithTranslation(string translated, string modelVersion, bool fromCache) => this with
        {
            Status = PlanStatus.Done,
            Outcome = RegionTranslationStatus.Translated,
            Translated = translated,
            ModelVersion = modelVersion,
            FromCache = fromCache,
            Message = null,
        };

        public RegionTranslation ToRegionTranslation(ScreenTextRegion region) => new()
        {
            Region = region,
            Status = Outcome,
            TranslatedText = Outcome == RegionTranslationStatus.Translated ? Translated : null,
            SourceLanguage = Source,
            SourceChosenByUser = ChosenByUser,
            DetectionConfidence = Confidence,
            Alternatives = Alternatives,
            Message = Message,
            RequiredPackage = RequiredPackage,
            FromCache = FromCache,
            ModelVersion = ModelVersion,
        };
    }
}
