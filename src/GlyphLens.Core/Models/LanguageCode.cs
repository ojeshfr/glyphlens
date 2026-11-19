using System.Text.RegularExpressions;

namespace GlyphLens.Core.Models;

/// <summary>
/// Validation helpers for language identifiers used across GlyphLens.
/// </summary>
/// <remarks>
/// Languages are identified by lowercase ISO 639-1/639-3 codes with optional BCP 47 subtags,
/// for example <c>en</c>, <c>pt-BR</c> or <c>zh-Hant</c>. This is a syntactic check only; whether
/// a language is actually supported depends on the installed OCR and translation models.
/// </remarks>
public static partial class LanguageCode
{
    /// <summary>The default translation target language.</summary>
    public const string English = "en";

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a syntactically valid language code.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && LanguageCodePattern().IsMatch(value);

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> is not a valid language code.</summary>
    public static string Validate(string? value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not a valid language code (expected e.g. 'en', 'pt-BR').", paramName);
        }

        return value!;
    }

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();
}
