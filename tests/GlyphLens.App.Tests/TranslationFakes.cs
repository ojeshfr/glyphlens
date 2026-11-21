using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

/// <summary>Raises recognition results on demand, like the OCR view model does.</summary>
internal sealed class FakeRecognisedSource : IRecognisedTextSource
{
    public event EventHandler<RegionsRecognisedEventArgs>? RegionsRecognised;

    public int SubscriberCount => RegionsRecognised?.GetInvocationList().Length ?? 0;

    public void Raise(params ScreenTextRegion[] regions) => RegionsRecognised?.Invoke(this, new RegionsRecognisedEventArgs(regions));

    public void RaiseDiscarded() => RegionsRecognised?.Invoke(this, new RegionsRecognisedEventArgs([], discarded: true));

    public static ScreenTextRegion Region(string text, int x = 10, int y = 20, int width = 120, int height = 20) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch);
}

internal sealed class FakeLanguageDetector : ILanguageDetector
{
    private readonly Dictionary<string, LanguageDetectionResult> _results = new(StringComparer.Ordinal);
    private int _texts;

    public int TextsDetected => Volatile.Read(ref _texts);

    public void Set(string text, string language, double? confidence = 0.95) =>
        _results[text] = LanguageDetectionResult.Detected(language, confidence);

    public Task<IReadOnlyList<LanguageDetectionResult>> DetectAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _texts, texts.Count);
        IReadOnlyList<LanguageDetectionResult> results = texts
            .Select(t => _results.TryGetValue(t, out var r) ? r : LanguageDetectionResult.Undetermined(LanguageDetectionIssue.Unknown))
            .ToList();
        return Task.FromResult(results);
    }
}

internal sealed class FakeTranslationEngine : ITranslationEngine
{
    private int _texts;
    private int _calls;

    public int TextsTranslated => Volatile.Read(ref _texts);

    public int Calls => Volatile.Read(ref _calls);

    public TranslationException? Failure { get; set; }

    /// <summary>When set, a translation waits for this task (or cancellation) before it completes.</summary>
    public Task? Hold { get; set; }

    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetStarted() => Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var outcomes = await TranslateBatchAsync([request], cancellationToken);
        return outcomes[0].Result ?? throw new TranslationException(outcomes[0].Error!);
    }

    public async Task<IReadOnlyList<TranslationOutcome>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _texts, requests.Count);
        Started.TrySetResult();
        if (Hold is { } hold)
        {
            await hold.WaitAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return requests
            .Select(r => TranslationOutcome.Success(new TranslationResult(r.Text, $"EN:{r.Text}", r.SourceLanguage!, r.TargetLanguage, "fake")))
            .ToList();
    }
}

internal sealed class FakeModelCatalog : IModelCatalog
{
    private readonly List<TranslationPackageInfo> _packages = [];

    public event EventHandler? Changed;

    public string? StoragePath { get; set; } = @"C:\Users\test\AppData\Local\GlyphLens\models";

    public WorkerException? CatalogFailure { get; set; }

    public TranslationException? ImportFailure { get; set; }

    public TranslationPackageInfo? ImportResult { get; set; }

    public List<string> Imported { get; } = [];

    public int CatalogCalls { get; private set; }

    public void Add(string from, string to, string version = "1.9") =>
        _packages.Add(new TranslationPackageInfo(from, to, from.ToUpperInvariant(), to.ToUpperInvariant(), version, $"translate-{from}_{to}"));

    public Task<ModelCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        CatalogCalls++;
        if (CatalogFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new ModelCatalog("argos-translate", StoragePath, [.. _packages], []));
    }

    public Task<RouteStatus> GetRouteStatusAsync(string source, string target, CancellationToken cancellationToken)
    {
        var package = _packages.FirstOrDefault(p => p.From == source && p.To == target);
        return Task.FromResult(
            package is null
                ? new RouteStatus(source, target, false, null, null, $"translate-{source}_{target}", RouteIssue.ModelMissing)
                : new RouteStatus(source, target, true, [source, target], $"argos:{source}-{target}@{package.Version}", null, RouteIssue.None));
    }

    public Task<TranslationPackageInfo> ImportPackageAsync(string localFilePath, CancellationToken cancellationToken)
    {
        Imported.Add(localFilePath);
        if (ImportFailure is { } failure)
        {
            throw failure;
        }

        var result = ImportResult ?? new TranslationPackageInfo("de", "en", "German", "English", "1.9", "translate-de_en");
        _packages.Add(result);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(result);
    }
}

internal sealed class FakeModelFilePicker : IModelFilePicker
{
    public string? Path { get; set; }

    public int Calls { get; private set; }

    public string? PickModelFile()
    {
        Calls++;
        return Path;
    }
}
