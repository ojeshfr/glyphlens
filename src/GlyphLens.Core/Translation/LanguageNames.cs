using System.Globalization;

namespace GlyphLens.Core.Translation;

/// <summary>Human-readable names for language codes, for messages and pickers.</summary>
public static class LanguageNames
{
    // Argos uses a few codes that are not .NET cultures.
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zt"] = "Chinese (Traditional)",
        ["zh"] = "Chinese",
        ["pb"] = "Portuguese (Brazil)",
    };

    /// <summary>The English name of <paramref name="code"/> (for example <c>German</c> for <c>de</c>), or the code itself when unknown.</summary>
    public static string Display(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "unknown";
        }

        if (Overrides.TryGetValue(code, out string? known))
        {
            return known;
        }

        try
        {
            string name = CultureInfo.GetCultureInfo(code).EnglishName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>The primary language subtag (<c>zh</c> for <c>zh-Hant</c>), lowercase.</summary>
    public static string Primary(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        int dash = code.IndexOf('-', StringComparison.Ordinal);
        return (dash < 0 ? code : code[..dash]).ToLowerInvariant();
    }

    /// <summary>Whether two codes denote the same primary language (<c>en</c> and <c>en-US</c> do).</summary>
    public static bool SamePrimary(string a, string b) => string.Equals(Primary(a), Primary(b), StringComparison.Ordinal);
}
