namespace GlyphLens.Infrastructure.IPC;

/// <summary>
/// Message types defined by protocol version 1. See docs/IPC-PROTOCOL.md.
/// </summary>
/// <remarks>
/// Successful responses use the request type with a <see cref="ResultSuffix"/> (for example
/// <c>worker.health.result</c>); failures use <see cref="Error"/>. Both echo the request id.
/// </remarks>
public static class WorkerMessageTypes
{
    public const string WorkerInitialize = "worker.initialize";
    public const string WorkerHealth = "worker.health";
    public const string WorkerShutdown = "worker.shutdown";
    public const string OcrDetect = "ocr.detect";
    public const string TranslationTranslate = "translation.translate";
    public const string TranslationCancel = "translation.cancel";
    public const string ModelsList = "models.list";
    public const string ModelsStatus = "models.status";
    public const string ModelsInstall = "models.install";
    public const string LanguageDetect = "language.detect";

    public const string Error = "error";
    public const string ResultSuffix = ".result";

    /// <summary>All request types defined by protocol version 1.0.</summary>
    public static IReadOnlySet<string> Requests { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkerInitialize,
        WorkerHealth,
        WorkerShutdown,
        OcrDetect,
        TranslationTranslate,
        TranslationCancel,
        ModelsList,
        ModelsStatus,
        ModelsInstall,
        LanguageDetect,
    };
}
