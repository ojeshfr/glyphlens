using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using GlyphLens.Core.Models;

namespace GlyphLens.Infrastructure.Translation;

/// <summary>
/// The wire format of the translation messages (<c>language.detect</c>, <c>translation.translate</c>, <c>models.*</c>): the
/// request payload shapes, strict parsing of the responses, and the mapping of worker failures to translation failures.
/// See "Translation and language identification" in docs/IPC-PROTOCOL.md.
/// </summary>
/// <remarks>
/// <para>
/// Request payloads are plain records. The channel serialises them with <c>WorkerMessageSerializer.Options</c>, so property
/// names become camelCase (<c>TargetLanguage</c> is <c>targetLanguage</c>).
/// </para>
/// <para>
/// Parsing is strict about shape: a missing or mistyped field, a wrong count, an id out of order or an unknown status is a
/// <see cref="WorkerErrorKind.ProtocolViolation"/>, never a guessed result. Unknown extra fields are ignored. The messages of
/// the violations name fields only; they never contain values, because values are private screen content.
/// </para>
/// </remarks>
internal static class TranslationWire
{
    private const int MaxDisplayLength = 128;
    private const int MaxProblemLength = 300;
    private const int MaxProblems = 50;
    private const int MaxModelVersionLength = 256;

    // ---- request payloads -------------------------------------------------------------------------------------------

    internal sealed record DetectRequest(IReadOnlyList<string> Texts);

    internal sealed record TranslateItemRequest(string Id, string Text, string SourceLanguage);

    internal sealed record TranslateRequest(string TargetLanguage, IReadOnlyList<TranslateItemRequest> Items);

    internal sealed record RouteStatusRequest(string Source, string Target);

    internal sealed record InstallRequest(string Path);

    // ---- language.detect ---------------------------------------------------------------------------------------------

    /// <summary>Parses a <c>language.detect.result</c> that must hold exactly <paramref name="expectedCount"/> results.</summary>
    internal static IReadOnlyList<LanguageDetectionResult> ParseDetectResults(JsonElement? payload, int expectedCount)
    {
        var root = RequireObject(payload, "language detection");
        var results = RequireArray(root, "results", string.Empty);
        var length = results.GetArrayLength();
        if (length != expectedCount)
        {
            throw Violation($"The language detection response has {length} results for {expectedCount} texts.");
        }

        var parsed = new List<LanguageDetectionResult>(length);
        var index = 0;
        foreach (var item in results.EnumerateArray())
        {
            parsed.Add(ParseDetectResult(item, $"results[{index}]"));
            index++;
        }

        return parsed;
    }

    private static LanguageDetectionResult ParseDetectResult(JsonElement item, string path)
    {
        RequireObjectElement(item, path);

        var language = OptionalString(item, "language", path);
        if (language is not null && !LanguageCode.IsValid(language))
        {
            throw Violation($"'{path}.language' is not a valid language code.");
        }

        // The confidence is the detector's own probability. It is validated even where it is then dropped.
        var confidence = OptionalProbability(item, "confidence", path);
        var issue = RequireString(item, "issue", path) switch
        {
            "none" => LanguageDetectionIssue.None,
            "too_short" => LanguageDetectionIssue.TooShort,
            "no_text" => LanguageDetectionIssue.NoText,
            "unknown" => LanguageDetectionIssue.Unknown,
            _ => throw Violation($"'{path}.issue' is not a known value."),
        };
        var alternatives = ParseAlternatives(item, path);

        if (language is null)
        {
            if (issue == LanguageDetectionIssue.None)
            {
                throw Violation($"'{path}' has no language and no reason.");
            }

            return LanguageDetectionResult.Undetermined(issue, alternatives);
        }

        if (issue != LanguageDetectionIssue.None)
        {
            throw Violation($"'{path}' has a language and also reports a problem.");
        }

        return LanguageDetectionResult.Detected(language, confidence, alternatives);
    }

    private static List<LanguageCandidate> ParseAlternatives(JsonElement item, string path)
    {
        var candidates = new List<LanguageCandidate>();
        if (!item.TryGetProperty("alternatives", out var alternatives) || alternatives.ValueKind == JsonValueKind.Null)
        {
            return candidates;
        }

        if (alternatives.ValueKind != JsonValueKind.Array)
        {
            throw Violation($"'{path}.alternatives' is not an array.");
        }

        var index = 0;
        foreach (var alternative in alternatives.EnumerateArray())
        {
            var alternativePath = $"{path}.alternatives[{index}]";
            RequireObjectElement(alternative, alternativePath);
            var language = RequireLanguage(alternative, "language", alternativePath);
            var confidence = OptionalProbability(alternative, "confidence", alternativePath)
                ?? throw Violation($"'{alternativePath}.confidence' is missing.");
            candidates.Add(new LanguageCandidate(language, confidence));
            index++;
        }

        return candidates;
    }

    // ---- translation.translate ---------------------------------------------------------------------------------------

    /// <summary>
    /// One entry of a <c>translation.translate.result</c>: either a translation (<see cref="IsOk"/>) or the worker's error code
    /// for that item.
    /// </summary>
    internal sealed record TranslateItemResponse
    {
        private TranslateItemResponse()
        {
        }

        public bool IsOk => ErrorCode is null;

        public string? TranslatedText { get; private init; }

        public string? SourceLanguage { get; private init; }

        public string? TargetLanguage { get; private init; }

        public string? ModelVersion { get; private init; }

        public string? ErrorCode { get; private init; }

        public string? RequiredPackage { get; private init; }

        public static TranslateItemResponse Ok(string translatedText, string sourceLanguage, string targetLanguage, string modelVersion) =>
            new()
            {
                TranslatedText = translatedText,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                ModelVersion = modelVersion,
            };

        public static TranslateItemResponse Error(string code, string? requiredPackage) =>
            new() { ErrorCode = code, RequiredPackage = requiredPackage };
    }

    /// <summary>
    /// Parses a <c>translation.translate.result</c>: one entry per sent item, in order, with the ids that were sent.
    /// </summary>
    /// <param name="payload">The response payload.</param>
    /// <param name="expectedIds">The ids of the items that were sent, in order.</param>
    internal static IReadOnlyList<TranslateItemResponse> ParseTranslateResults(JsonElement? payload, IReadOnlyList<string> expectedIds)
    {
        var root = RequireObject(payload, "translation");
        var results = RequireArray(root, "results", string.Empty);
        var length = results.GetArrayLength();
        if (length != expectedIds.Count)
        {
            throw Violation($"The translation response has {length} results for {expectedIds.Count} items.");
        }

        var parsed = new List<TranslateItemResponse>(length);
        var index = 0;
        foreach (var item in results.EnumerateArray())
        {
            var path = $"results[{index}]";
            RequireObjectElement(item, path);

            if (!string.Equals(RequireString(item, "id", path), expectedIds[index], StringComparison.Ordinal))
            {
                throw Violation($"'{path}.id' does not match the request; results must come back in request order with the ids that were sent.");
            }

            switch (RequireString(item, "status", path))
            {
                case "ok":
                    parsed.Add(ParseTranslatedItem(item, path));
                    break;
                case "error":
                    parsed.Add(ParseFailedItem(item, path));
                    break;
                default:
                    throw Violation($"'{path}.status' is not 'ok' or 'error'.");
            }

            index++;
        }

        return parsed;
    }

    private static TranslateItemResponse ParseTranslatedItem(JsonElement item, string path)
    {
        var translated = RequireString(item, "translatedText", path);
        var source = RequireLanguage(item, "sourceLanguage", path);
        var target = RequireLanguage(item, "targetLanguage", path);
        var modelVersion = RequireString(item, "modelVersion", path);
        if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion.Length > MaxModelVersionLength)
        {
            throw Violation($"'{path}.modelVersion' is blank or too long.");
        }

        return TranslateItemResponse.Ok(translated, source, target, modelVersion);
    }

    private static TranslateItemResponse ParseFailedItem(JsonElement item, string path)
    {
        if (!item.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            throw Violation($"'{path}.error' is missing or not an object.");
        }

        var errorPath = path + ".error";
        var code = RequireString(error, "code", errorPath);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw Violation($"'{errorPath}.code' is blank.");
        }

        // The package name is a hint shown to the user; anything that does not look like one is dropped, not repeated.
        var requiredPackage = OptionalString(error, "requiredPackage", errorPath);
        return TranslateItemResponse.Error(code, requiredPackage is not null && IsSafePackageName(requiredPackage) ? requiredPackage : null);
    }

    /// <summary>Maps the worker's per-item error code to a translation error category. Unknown codes are <see cref="TranslationErrorKind.Failed"/>.</summary>
    internal static TranslationErrorKind MapItemErrorCode(string code) => code switch
    {
        "model_missing" => TranslationErrorKind.ModelMissing,
        "unsupported_language_pair" => TranslationErrorKind.UnsupportedLanguagePair,
        "same_language" => TranslationErrorKind.SameLanguage,
        "empty_text" => TranslationErrorKind.EmptyText,
        "text_too_long" => TranslationErrorKind.TextTooLong,
        _ => TranslationErrorKind.Failed,
    };

    /// <summary>
    /// Maps a failure of the worker channel to a request-level translation failure. The message is fixed text; the worker's
    /// own message stays on the inner exception.
    /// </summary>
    internal static TranslationException ToTranslationException(WorkerException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var (kind, message) = ex.Kind switch
        {
            WorkerErrorKind.Rejected when string.Equals(ex.ErrorCode, "engine_unavailable", StringComparison.Ordinal) =>
                (TranslationErrorKind.EngineUnavailable, "The translation engine is not available."),
            WorkerErrorKind.Rejected =>
                (TranslationErrorKind.Failed, "The worker rejected the translation request."),
            WorkerErrorKind.ProtocolViolation =>
                (TranslationErrorKind.Failed, "The worker sent an unexpected response."),
            _ => (TranslationErrorKind.WorkerUnavailable, "The translation worker is not available."),
        };

        return new TranslationException(new TranslationError(kind, message), ex);
    }

    // ---- models.list / models.status / models.install ----------------------------------------------------------------

    internal static ModelCatalog ParseModelCatalog(JsonElement? payload)
    {
        var root = RequireObject(payload, "model catalog");

        var engine = RequireString(root, "engine", string.Empty);
        if (string.IsNullOrWhiteSpace(engine))
        {
            throw Violation("'engine' is blank.");
        }

        var state = RequireString(root, "state", string.Empty);
        if (state is not ("unavailable" or "not_installed" or "installed"))
        {
            throw Violation("'state' is not a known value.");
        }

        var storagePath = OptionalString(root, "storagePath", string.Empty);

        var packages = new List<TranslationPackageInfo>();
        if (root.TryGetProperty("packages", out var packageArray) && packageArray.ValueKind != JsonValueKind.Null)
        {
            if (packageArray.ValueKind != JsonValueKind.Array)
            {
                throw Violation("'packages' is not an array.");
            }

            var index = 0;
            foreach (var package in packageArray.EnumerateArray())
            {
                packages.Add(ParsePackage(package, $"packages[{index}]"));
                index++;
            }
        }
        else if (state != "unavailable")
        {
            throw Violation("'packages' is missing.");
        }

        return new ModelCatalog(
            Clean(engine, MaxDisplayLength),
            storagePath is null ? null : Clean(storagePath, 1024),
            packages,
            ParseProblems(root));
    }

    private static List<string> ParseProblems(JsonElement root)
    {
        var problems = new List<string>();
        if (!root.TryGetProperty("problems", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return problems;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Violation("'problems' is not an array.");
        }

        var index = 0;
        foreach (var problem in array.EnumerateArray())
        {
            if (problem.ValueKind != JsonValueKind.String)
            {
                throw Violation($"'problems[{index}]' is not a string.");
            }

            if (problems.Count < MaxProblems)
            {
                problems.Add(Clean(problem.GetString()!, MaxProblemLength));
            }

            index++;
        }

        return problems;
    }

    internal static TranslationPackageInfo ParsePackage(JsonElement element, string path)
    {
        RequireObjectElement(element, path);

        var from = RequireLanguage(element, "from", path);
        var to = RequireLanguage(element, "to", path);
        var fromName = RequireString(element, "fromName", path);
        var toName = RequireString(element, "toName", path);
        var version = RequireString(element, "version", path);
        var packageName = RequireString(element, "packageName", path);
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(packageName))
        {
            throw Violation($"'{path}' has a blank version or package name.");
        }

        // The names come from the package file the user chose, so they are bounded and stripped of control characters.
        return new TranslationPackageInfo(
            from,
            to,
            Clean(fromName, MaxDisplayLength),
            Clean(toName, MaxDisplayLength),
            Clean(version, MaxDisplayLength),
            Clean(packageName, MaxDisplayLength));
    }

    internal static RouteStatus ParseRouteStatus(JsonElement? payload)
    {
        var root = RequireObject(payload, "route status");

        var source = RequireLanguage(root, "source", string.Empty);
        var target = RequireLanguage(root, "target", string.Empty);
        if (!root.TryGetProperty("available", out var availableElement) || availableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Violation("'available' is missing or not a boolean.");
        }

        var available = availableElement.GetBoolean();
        var route = ParseRoute(root);
        var modelVersion = OptionalString(root, "modelVersion", string.Empty);
        var missingPackage = OptionalString(root, "missingPackage", string.Empty);
        var issue = RequireString(root, "issue", string.Empty) switch
        {
            "none" => RouteIssue.None,
            "model_missing" => RouteIssue.ModelMissing,
            "unsupported_pair" => RouteIssue.UnsupportedPair,
            "engine_unavailable" => RouteIssue.EngineUnavailable,
            _ => throw Violation("'issue' is not a known value."),
        };

        if (available)
        {
            // The model version keys the translation cache; an available route without one cannot be cached or trusted.
            if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion.Length > MaxModelVersionLength)
            {
                throw Violation("An available route has no usable 'modelVersion'.");
            }

            if (issue != RouteIssue.None)
            {
                throw Violation("An available route also reports an issue.");
            }
        }
        else if (issue == RouteIssue.None)
        {
            throw Violation("An unavailable route does not say why.");
        }

        return new RouteStatus(
            source,
            target,
            available,
            route,
            available ? modelVersion : null,
            missingPackage is not null && IsSafePackageName(missingPackage) ? missingPackage : null,
            issue);
    }

    private static List<string> ParseRoute(JsonElement root)
    {
        var route = new List<string>();
        if (!root.TryGetProperty("route", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return route;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Violation("'route' is not an array.");
        }

        var index = 0;
        foreach (var hop in array.EnumerateArray())
        {
            if (hop.ValueKind != JsonValueKind.String || !LanguageCode.IsValid(hop.GetString()))
            {
                throw Violation($"'route[{index}]' is not a valid language code.");
            }

            route.Add(hop.GetString()!);
            index++;
        }

        return route;
    }

    internal static TranslationPackageInfo ParseInstallResult(JsonElement? payload)
    {
        var root = RequireObject(payload, "model install");
        if (!root.TryGetProperty("package", out var package))
        {
            throw Violation("'package' is missing.");
        }

        return ParsePackage(package, "package");
    }

    /// <summary>
    /// Maps a worker error answering <c>models.install</c> to a <see cref="TranslationException"/> with a readable, fixed
    /// message. Other failures (the worker is not reachable, an unexpected error) are not mapped and stay worker exceptions.
    /// </summary>
    internal static bool TryMapInstallError(WorkerException ex, [NotNullWhen(true)] out TranslationException? mapped)
    {
        ArgumentNullException.ThrowIfNull(ex);

        mapped = null;
        if (ex.Kind != WorkerErrorKind.Rejected)
        {
            return false;
        }

        TranslationError? error = ex.ErrorCode switch
        {
            "package_not_found" => new TranslationError(TranslationErrorKind.Failed, "The worker could not find the package file."),
            "invalid_package" => new TranslationError(TranslationErrorKind.Failed, "The file is not a valid translation package (.argosmodel)."),
            "unsupported_package" => new TranslationError(TranslationErrorKind.Failed, "This package uses a format this version cannot run yet (for example a BPE tokenizer)."),
            "install_failed" => new TranslationError(TranslationErrorKind.Failed, "The package could not be installed."),
            "engine_unavailable" => new TranslationError(TranslationErrorKind.EngineUnavailable, "The translation engine is not available, so no package can be installed."),
            "invalid_request" => new TranslationError(TranslationErrorKind.Failed, "The worker rejected the package path."),
            _ => null,
        };
        if (error is null)
        {
            return false;
        }

        mapped = new TranslationException(error, ex);
        return true;
    }

    // ---- shared JSON helpers -----------------------------------------------------------------------------------------

    /// <summary>The first <paramref name="maxLength"/> UTF-16 units of <paramref name="value"/>, never ending inside a surrogate pair.</summary>
    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    /// <summary>Bounds <paramref name="value"/> and replaces control characters, for text that comes from outside (package metadata, worker diagnostics).</summary>
    internal static string Clean(string value, int maxLength)
    {
        var bounded = Truncate(value, maxLength);
        return string.Create(bounded.Length, bounded, static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private static bool IsSafePackageName(string value)
    {
        if (value.Length is 0 or > MaxDisplayLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static WorkerException Violation(string message) =>
        new(WorkerErrorKind.ProtocolViolation, "The worker's response is malformed: " + message);

    private static JsonElement RequireObject(JsonElement? payload, string what)
    {
        if (payload is { ValueKind: JsonValueKind.Object } root)
        {
            return root;
        }

        throw Violation($"The {what} response has no payload object.");
    }

    private static void RequireObjectElement(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Violation($"'{path}' is not an object.");
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static JsonElement RequireArray(JsonElement parent, string name, string path)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        throw Violation($"'{Join(path, name)}' is missing or not an array.");
    }

    private static string RequireString(JsonElement parent, string name, string path)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw Violation($"'{Join(path, name)}' is missing or not a string.");
    }

    private static string? OptionalString(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Violation($"'{Join(path, name)}' is not a string.");
    }

    private static string RequireLanguage(JsonElement parent, string name, string path)
    {
        var value = RequireString(parent, name, path);
        return LanguageCode.IsValid(value)
            ? value
            : throw Violation($"'{Join(path, name)}' is not a valid language code.");
    }

    private static double? OptionalProbability(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || number < 0.0 || number > 1.0)
        {
            throw Violation($"'{Join(path, name)}' is not a number between 0 and 1.");
        }

        return number;
    }

    /// <summary>The id the client gives item <paramref name="index"/> of a chunk.</summary>
    internal static string ItemId(int index) => index.ToString(CultureInfo.InvariantCulture);
}
