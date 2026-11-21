namespace GlyphLens.Infrastructure.Translation;

/// <summary>
/// Timeouts and limits of the translation clients (<see cref="WorkerTranslationEngine"/>, <see cref="WorkerLanguageDetector"/>,
/// <see cref="WorkerModelCatalog"/>). The defaults follow docs/IPC-PROTOCOL.md; every setter validates its value.
/// </summary>
public sealed record TranslationClientOptions
{
    /// <summary>The most texts the worker accepts in one <c>language.detect</c> request.</summary>
    public const int MaxDetectBatchSize = 64;

    /// <summary>The most items the worker accepts in one <c>translation.translate</c> request.</summary>
    public const int MaxTranslateChunkSize = 32;

    /// <summary>The longest text the worker accepts, in characters (the client counts UTF-16 units, which is never fewer).</summary>
    public const int MaxTextLengthLimit = 4096;

    private readonly TimeSpan _detectTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _translateTimeout = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _modelsTimeout = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _installTimeout = TimeSpan.FromSeconds(180);
    private readonly int _chunkSize = 8;
    private readonly int _maxTextLength = MaxTextLengthLimit;

    /// <summary>How long a <c>language.detect</c> request may take.</summary>
    public TimeSpan DetectTimeout
    {
        get => _detectTimeout;
        init => _detectTimeout = RequirePositive(value);
    }

    /// <summary>
    /// How long one <c>translation.translate</c> request (one chunk) may take. The first request after start-up loads the engine
    /// and can take several seconds.
    /// </summary>
    public TimeSpan TranslateTimeout
    {
        get => _translateTimeout;
        init => _translateTimeout = RequirePositive(value);
    }

    /// <summary>How long <c>models.list</c> and <c>models.status</c> may take (the first call loads the engine).</summary>
    public TimeSpan ModelsTimeout
    {
        get => _modelsTimeout;
        init => _modelsTimeout = RequirePositive(value);
    }

    /// <summary>How long <c>models.install</c> may take (it unpacks and validates the archive).</summary>
    public TimeSpan InstallTimeout
    {
        get => _installTimeout;
        init => _installTimeout = RequirePositive(value);
    }

    /// <summary>
    /// The most items sent in one <c>translation.translate</c> request. Small chunks keep a stale request short, which is how
    /// translation is "cancelled" (the worker cannot read a cancel while it translates). Range 1 to <see cref="MaxTranslateChunkSize"/>.
    /// </summary>
    public int ChunkSize
    {
        get => _chunkSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTranslateChunkSize);
            _chunkSize = value;
        }
    }

    /// <summary>
    /// The longest text, in UTF-16 units, the translation engine sends (longer text fails with a per-item
    /// <c>TextTooLong</c>) and the language detector sends (longer text is cut, which is enough to identify a language).
    /// Range 1 to <see cref="MaxTextLengthLimit"/>.
    /// </summary>
    public int MaxTextLength
    {
        get => _maxTextLength;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTextLengthLimit);
            _maxTextLength = value;
        }
    }

    private static TimeSpan RequirePositive(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        return value;
    }
}
