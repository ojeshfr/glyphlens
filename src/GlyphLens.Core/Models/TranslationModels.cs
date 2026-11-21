namespace GlyphLens.Core.Models;

/// <summary>Category of a translation failure for one text.</summary>
public enum TranslationErrorKind
{
    /// <summary>No installed model translates this language pair. <see cref="TranslationError.RequiredPackage"/> says which one is needed.</summary>
    ModelMissing = 1,

    /// <summary>The engine has no route for this pair, even with every known package.</summary>
    UnsupportedLanguagePair,

    /// <summary>The source language was not given and could not be identified.</summary>
    LanguageUndetermined,

    /// <summary>The source and target language are the same; there is nothing to translate.</summary>
    SameLanguage,

    /// <summary>The text is empty or whitespace.</summary>
    EmptyText,

    /// <summary>The text exceeds the per-item limit.</summary>
    TextTooLong,

    /// <summary>The Python worker is not running or did not answer.</summary>
    WorkerUnavailable,

    /// <summary>The translation engine could not be loaded (dependency missing or failed to initialise).</summary>
    EngineUnavailable,

    /// <summary>The engine failed on this text.</summary>
    Failed,
}

/// <summary>A translation failure. The message is safe to show and never contains the text that was translated.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Message">A user-readable explanation.</param>
/// <param name="RequiredPackage">For <see cref="TranslationErrorKind.ModelMissing"/>: the Argos package that would fix it (for example <c>translate-de_en</c>).</param>
public sealed record TranslationError(TranslationErrorKind Kind, string Message, string? RequiredPackage = null);

/// <summary>The result of translating one text: exactly one of <see cref="Result"/> and <see cref="Error"/> is set.</summary>
public sealed record TranslationOutcome
{
    private TranslationOutcome(TranslationResult? result, TranslationError? error)
    {
        Result = result;
        Error = error;
    }

    public TranslationResult? Result { get; }

    public TranslationError? Error { get; }

    public bool Succeeded => Result is not null;

    public static TranslationOutcome Success(TranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new TranslationOutcome(result, null);
    }

    public static TranslationOutcome Failure(TranslationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new TranslationOutcome(null, error);
    }
}

/// <summary>A request-level translation failure (for example the worker is down). Per-text failures are <see cref="TranslationOutcome"/>s.</summary>
public sealed class TranslationException : Exception
{
    public TranslationException()
        : this(new TranslationError(TranslationErrorKind.Failed, "Translation failed."))
    {
    }

    public TranslationException(string message)
        : this(new TranslationError(TranslationErrorKind.Failed, message))
    {
    }

    public TranslationException(string message, Exception innerException)
        : this(new TranslationError(TranslationErrorKind.Failed, message), innerException)
    {
    }

    public TranslationException(TranslationError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public TranslationError Error { get; }
}

/// <summary>One installed (or available) translation package, for example German to English.</summary>
public sealed record TranslationPackageInfo(
    string From,
    string To,
    string FromName,
    string ToName,
    string Version,
    string PackageName);

/// <summary>Why a route is not available.</summary>
public enum RouteIssue
{
    None = 0,

    /// <summary>The engine knows the pair but the required package is not installed.</summary>
    ModelMissing,

    /// <summary>The engine has no model for this pair at all.</summary>
    UnsupportedPair,

    /// <summary>The translation engine is not available.</summary>
    EngineUnavailable,
}

/// <summary>Whether, and by which route, one language pair can be translated right now.</summary>
public sealed record RouteStatus
{
    public RouteStatus(
        string source,
        string target,
        bool available,
        IReadOnlyList<string>? route,
        string? modelVersion,
        string? missingPackage,
        RouteIssue issue)
    {
        LanguageCode.Validate(source, nameof(source));
        LanguageCode.Validate(target, nameof(target));

        if (available && string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new ArgumentException("An available route needs a model version (it keys the translation cache).", nameof(modelVersion));
        }

        Source = source;
        Target = target;
        Available = available;
        Route = route ?? [];
        ModelVersion = modelVersion;
        MissingPackage = missingPackage;
        Issue = available ? RouteIssue.None : (issue == RouteIssue.None ? RouteIssue.ModelMissing : issue);
    }

    public string Source { get; }

    public string Target { get; }

    public bool Available { get; }

    /// <summary>The languages the translation passes through, for example <c>de, en</c> or <c>de, en, fr</c> via a pivot.</summary>
    public IReadOnlyList<string> Route { get; }

    /// <summary>Identifies the installed model(s) used, including versions. Changes when a package is installed or updated.</summary>
    public string? ModelVersion { get; }

    public string? MissingPackage { get; }

    public RouteIssue Issue { get; }
}

/// <summary>What the translation engine has installed, where, and which routes that allows.</summary>
public sealed record ModelCatalog(
    string Engine,
    string? StoragePath,
    IReadOnlyList<TranslationPackageInfo> Packages,
    IReadOnlyList<string> Problems)
{
    /// <summary>Source languages that have at least one installed package.</summary>
    public IEnumerable<string> InstalledSources => Packages.Select(p => p.From).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any package translates into <paramref name="target"/>.</summary>
    public bool HasTarget(string target) => Packages.Any(p => string.Equals(p.To, target, StringComparison.OrdinalIgnoreCase));
}
