using System.Text;

namespace GlyphLens.Core.Translation;

/// <summary>
/// Normalises recognised text so that trivial differences between OCR passes (whitespace, composed or decomposed accents)
/// do not defeat the translation cache or trigger a repeat translation.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Returns <paramref name="text"/> in Unicode normalisation form C with every run of whitespace (including line breaks)
    /// collapsed to one space and the ends trimmed. Letter case is kept: it can change meaning and translations.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        bool pendingSpace = false;

        foreach (char c in composed)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether <paramref name="text"/> contains a Han ideograph, a kana or a Hangul syllable. One such character is a complete
    /// word, unlike a single Latin letter, so short CJK text is worth identifying.
    /// </summary>
    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            if (v is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x20000 and <= 0x2EBEF) or (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF)
                or (>= 0xFF66 and <= 0xFF9F) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xAC00 and <= 0xD7AF))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The number of characters in <paramref name="text"/> that are letters (used to decide whether detection is meaningful).</summary>
    public static int CountLetters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                count++;
            }
        }

        return count;
    }
}
