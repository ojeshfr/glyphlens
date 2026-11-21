using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Translation;

/// <summary>
/// Identifies the language of texts with the local detector inside the Python worker (<c>language.detect</c>).
/// </summary>
/// <remarks>
/// <para>
/// Texts that cannot be identified anyway (empty, no letters, fewer than three letters) are answered locally with the same
/// verdict the worker would give, so a screen full of numbers and one-letter labels costs no round trip. The rest go to the
/// worker in requests of at most <see cref="TranslationClientOptions.MaxDetectBatchSize"/> texts, one request at a time. A text
/// longer than <see cref="TranslationClientOptions.MaxTextLength"/> is cut for detection (a prefix identifies a language as well
/// as the whole text).
/// </para>
/// <para>
/// The worker's answer is validated strictly. A response that does not match the request (count, language codes, confidences)
/// is a <see cref="WorkerErrorKind.ProtocolViolation"/>, never a made-up result. Texts are private screen content and are never
/// logged.
/// </para>
/// </remarks>
public sealed class WorkerLanguageDetector : ILanguageDetector
{
    private readonly IWorkerRequestChannel _channel;
    private readonly TranslationClientOptions _options;

    public WorkerLanguageDetector(IWorkerRequestChannel channel, TranslationClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _channel = channel;
        _options = options ?? new TranslationClientOptions();
    }

    /// <inheritdoc />
    /// <exception cref="WorkerException">The worker is not reachable, timed out, rejected the request, or answered with a malformed response.</exception>
    public async Task<IReadOnlyList<LanguageDetectionResult>> DetectAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new LanguageDetectionResult?[texts.Count];
        var remote = new List<int>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i] ?? throw new ArgumentException("The texts must not contain null.", nameof(texts));
            var local = AnswerLocally(text);
            if (local is null)
            {
                remote.Add(i);
            }
            else
            {
                results[i] = local;
            }
        }

        for (var offset = 0; offset < remote.Count; offset += TranslationClientOptions.MaxDetectBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(TranslationClientOptions.MaxDetectBatchSize, remote.Count - offset);
            var batch = new string[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = TranslationWire.Truncate(texts[remote[offset + i]], _options.MaxTextLength);
            }

            var response = await _channel
                .RequestAsync(WorkerMessageTypes.LanguageDetect, new TranslationWire.DetectRequest(batch), _options.DetectTimeout, cancellationToken)
                .ConfigureAwait(false);
            var parsed = TranslationWire.ParseDetectResults(response, count);
            for (var i = 0; i < count; i++)
            {
                results[remote[offset + i]] = parsed[i];
            }
        }

        return results!;
    }

    /// <summary>The verdict for a text the detector cannot decide anyway, or <see langword="null"/> when the worker has to look at it.</summary>
    private static LanguageDetectionResult? AnswerLocally(string text)
    {
        var letters = TextNormalizer.CountLetters(text);
        if (letters == 0)
        {
            return LanguageDetectionResult.Undetermined(LanguageDetectionIssue.NoText);
        }

        // One CJK character is a whole word ("好", "谢谢"), so short Chinese, Japanese and Korean text is worth asking the worker about.
        return letters < 3 && !TextNormalizer.ContainsCjk(text) ? LanguageDetectionResult.Undetermined(LanguageDetectionIssue.TooShort) : null;
    }
}
