using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Translation;

/// <summary>
/// Translates text with the local Argos engine inside the Python worker (<c>translation.translate</c>). It never contacts a
/// cloud service and never invents a translation.
/// </summary>
/// <remarks>
/// <para>
/// What can be decided without the worker is decided here and reported as a per-item failure: empty text, text over the
/// length limit, source equal to target, and a source language that is not given and cannot be identified. Only the remaining
/// items are sent, grouped by target language and split into requests of at most
/// <see cref="TranslationClientOptions.ChunkSize"/> items. Requests are sent one at a time (the worker handles one request at a
/// time, and a stale batch is abandoned by cancelling, so it must stay short); cancellation is honoured before every request and
/// no further chunk is sent once it is signalled. Concurrent calls on one engine take turns.
/// </para>
/// <para>
/// A failure of the whole batch (worker not running, timed out, engine unavailable, malformed response) throws a
/// <see cref="TranslationException"/>; <see cref="OperationCanceledException"/> is never wrapped. The worker's response is
/// validated strictly. Texts and translations are private screen content: they are never logged, and the exceptions' messages
/// are fixed text that never repeats them.
/// </para>
/// <para>
/// When no <see cref="ILanguageDetector"/> is supplied, a request without a source language fails with
/// <see cref="TranslationErrorKind.LanguageUndetermined"/>. When one is supplied, the engine uses its answer as it is, whatever
/// its confidence: callers that apply a confidence threshold or a user override detect first and pass the source language.
/// </para>
/// </remarks>
public sealed class WorkerTranslationEngine : ITranslationEngine, IDisposable
{
    private readonly IWorkerRequestChannel _channel;
    private readonly ILanguageDetector? _detector;
    private readonly TranslationClientOptions _options;
    private readonly SemaphoreSlim _turn = new(1, 1);

    public WorkerTranslationEngine(IWorkerRequestChannel channel, ILanguageDetector? detector = null, TranslationClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _channel = channel;
        _detector = detector;
        _options = options ?? new TranslationClientOptions();
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcomes = await TranslateBatchAsync([request], cancellationToken).ConfigureAwait(false);
        var outcome = outcomes[0];
        return outcome.Result ?? throw new TranslationException(outcome.Error!);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranslationOutcome>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();

        var outcomes = new TranslationOutcome?[requests.Count];
        var work = new List<WorkItem>(requests.Count);
        var needsDetection = new List<int>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i] ?? throw new ArgumentException("The batch must not contain null requests.", nameof(requests));
            if (CheckLocally(request) is { } failure)
            {
                outcomes[i] = TranslationOutcome.Failure(failure);
            }
            else if (request.SourceLanguage is null)
            {
                needsDetection.Add(i);
            }
            else
            {
                work.Add(new WorkItem(i, request, request.SourceLanguage));
            }
        }

        if (needsDetection.Count > 0 && _detector is null)
        {
            foreach (var index in needsDetection)
            {
                outcomes[index] = TranslationOutcome.Failure(Undetermined());
            }

            needsDetection.Clear();
        }

        if (work.Count > 0 || needsDetection.Count > 0)
        {
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (needsDetection.Count > 0)
                {
                    await ResolveSourcesAsync(requests, needsDetection, work, outcomes, cancellationToken).ConfigureAwait(false);
                }

                await SendChunksAsync(work, outcomes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }

        return outcomes!;
    }

    public void Dispose() => _turn.Dispose();

    /// <summary>Failures that need no worker: empty text, over-long text, same source and target.</summary>
    private TranslationError? CheckLocally(TranslationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new TranslationError(TranslationErrorKind.EmptyText, "There is no text to translate.");
        }

        if (request.Text.Length > _options.MaxTextLength)
        {
            return new TranslationError(TranslationErrorKind.TextTooLong, $"The text is longer than {_options.MaxTextLength} characters.");
        }

        return request.SourceLanguage is { } source && SameLanguage(source, request.TargetLanguage)
            ? SameLanguageError(source)
            : null;
    }

    /// <summary>Identifies the source language of the requests that gave none, and queues those that were identified.</summary>
    private async Task ResolveSourcesAsync(
        IReadOnlyList<TranslationRequest> requests,
        List<int> needsDetection,
        List<WorkItem> work,
        TranslationOutcome?[] outcomes,
        CancellationToken cancellationToken)
    {
        var texts = new string[needsDetection.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = requests[needsDetection[i]].Text;
        }

        IReadOnlyList<LanguageDetectionResult> detected;
        try
        {
            detected = await _detector!.DetectAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerException ex)
        {
            throw TranslationWire.ToTranslationException(ex);
        }

        if (detected.Count != texts.Length)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Failed, "The language detector returned an unexpected number of results."));
        }

        for (var i = 0; i < texts.Length; i++)
        {
            var index = needsDetection[i];
            var request = requests[index];
            if (detected[i].Language is not { } language)
            {
                outcomes[index] = TranslationOutcome.Failure(Undetermined());
            }
            else if (SameLanguage(language, request.TargetLanguage))
            {
                outcomes[index] = TranslationOutcome.Failure(SameLanguageError(language));
            }
            else
            {
                work.Add(new WorkItem(index, request, language));
            }
        }

        // Keep the original order: items with a given source were queued first, the detected ones after.
        work.Sort(static (a, b) => a.Index.CompareTo(b.Index));
    }

    private async Task SendChunksAsync(List<WorkItem> work, TranslationOutcome?[] outcomes, CancellationToken cancellationToken)
    {
        // One worker request has one target language, so items are grouped by target (in order of first appearance).
        foreach (var group in work.GroupBy(static item => item.Request.TargetLanguage, StringComparer.Ordinal))
        {
            foreach (var chunk in group.Chunk(_options.ChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TranslateChunkAsync(group.Key, chunk, outcomes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TranslateChunkAsync(string target, WorkItem[] chunk, TranslationOutcome?[] outcomes, CancellationToken cancellationToken)
    {
        var ids = new string[chunk.Length];
        var items = new TranslationWire.TranslateItemRequest[chunk.Length];
        for (var i = 0; i < chunk.Length; i++)
        {
            ids[i] = TranslationWire.ItemId(i);
            items[i] = new TranslationWire.TranslateItemRequest(ids[i], chunk[i].Request.Text, chunk[i].Source);
        }

        IReadOnlyList<TranslationWire.TranslateItemResponse> responses;
        try
        {
            var payload = await _channel
                .RequestAsync(WorkerMessageTypes.TranslationTranslate, new TranslationWire.TranslateRequest(target, items), _options.TranslateTimeout, cancellationToken)
                .ConfigureAwait(false);
            responses = TranslationWire.ParseTranslateResults(payload, ids);
        }
        catch (WorkerException ex)
        {
            throw TranslationWire.ToTranslationException(ex);
        }

        for (var i = 0; i < chunk.Length; i++)
        {
            outcomes[chunk[i].Index] = ToOutcome(chunk[i], target, responses[i]);
        }
    }

    private static TranslationOutcome ToOutcome(WorkItem item, string target, TranslationWire.TranslateItemResponse response)
    {
        if (response.IsOk)
        {
            return TranslationOutcome.Success(new TranslationResult(
                item.Request.Text,
                response.TranslatedText!,
                response.SourceLanguage!,
                response.TargetLanguage!,
                response.ModelVersion!));
        }

        var code = response.ErrorCode!;
        var kind = TranslationWire.MapItemErrorCode(code);
        return TranslationOutcome.Failure(kind switch
        {
            TranslationErrorKind.ModelMissing => new TranslationError(kind, $"No installed model translates {item.Source} to {target}.", response.RequiredPackage),
            TranslationErrorKind.UnsupportedLanguagePair => new TranslationError(kind, $"The translation engine does not support {item.Source} to {target}."),
            TranslationErrorKind.SameLanguage => SameLanguageError(item.Source),
            TranslationErrorKind.EmptyText => new TranslationError(kind, "There is no text to translate."),
            TranslationErrorKind.TextTooLong => new TranslationError(kind, "The text is too long for the translation engine."),
            _ => new TranslationError(TranslationErrorKind.Failed, "The translation failed."),
        });
    }

    private static bool SameLanguage(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static TranslationError SameLanguageError(string language) =>
        new(TranslationErrorKind.SameLanguage, $"The text is already in {language}; there is nothing to translate.");

    private static TranslationError Undetermined() =>
        new(TranslationErrorKind.LanguageUndetermined, "The source language is not known and could not be identified.");

    private sealed record WorkItem(int Index, TranslationRequest Request, string Source);
}
