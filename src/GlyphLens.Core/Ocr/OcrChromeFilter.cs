using GlyphLens.Core.Models;

namespace GlyphLens.Core.Ocr;

/// <summary>What <see cref="OcrChromeFilter"/> kept and how many recognised items it set aside.</summary>
/// <param name="Kept">The regions to use, in their original order.</param>
/// <param name="Skipped">How many items were window chrome (the close button glyph, the window's own title).</param>
public sealed record OcrChromeFilterResult(IReadOnlyList<ScreenTextRegion> Kept, int Skipped);

/// <summary>
/// Sets aside the few things OCR reads from a window's own frame that are not content: the title-bar button glyphs (a close "X",
/// minimise, maximise) and the window's own title.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow, so real content is never dropped: only items that lie completely inside the title strip at the top of the
/// frame are considered, and only three kinds are removed. (1) A one- to three-glyph group of window-button look-alikes in the
/// right-hand third of the strip. (2) Up to three characters of pure punctuation in the strip (the application icon read as ":" or
/// "."). (3) Text that is the window's own title (as the capture target reports it) in the strip. Everything else, including Chinese
/// text in the strip that is not the title, is kept. The caller reports the number skipped, so the
/// user can see that something was set aside.
/// </para>
/// </remarks>
public static class OcrChromeFilter
{
    private const double StripFraction = 0.07;
    private const int MinStripHeight = 32;
    private const int MaxStripHeight = 72;
    private const double RightThirdStart = 2.0 / 3.0;

    // Window-button look-alikes as OCR reads them: close, minimise, maximise/restore.
    private static readonly HashSet<char> ControlGlyphs =
    [
        'X', 'x', '×', '✕', '✖', '╳', '–', '—', '−', '-', '_', '+',
        '□', '☐', '▭', '口', '回',
    ];

    /// <summary>The height, in pixels, of the strip at the top of a frame in which window chrome is looked for.</summary>
    public static int StripHeight(int frameHeight) =>
        Math.Clamp((int)Math.Round(frameHeight * StripFraction), MinStripHeight, MaxStripHeight);

    /// <summary>Applies the filter.</summary>
    /// <param name="regions">The recognised regions (positions relative to the frame).</param>
    /// <param name="frameWidth">The frame width in pixels.</param>
    /// <param name="frameHeight">The frame height in pixels.</param>
    /// <param name="windowTitle">The title of the captured window, or <see langword="null"/> for a display capture (no title bar to recognise).</param>
    public static OcrChromeFilterResult Filter(IReadOnlyList<ScreenTextRegion> regions, int frameWidth, int frameHeight, string? windowTitle)
    {
        ArgumentNullException.ThrowIfNull(regions);

        if (regions.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new OcrChromeFilterResult(regions, 0);
        }

        int strip = StripHeight(frameHeight);
        string title = Normalise(windowTitle);
        var kept = new List<ScreenTextRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (IsChrome(region, frameWidth, strip, title))
            {
                continue;
            }

            kept.Add(region);
        }

        return kept.Count == regions.Count ? new OcrChromeFilterResult(regions, 0) : new OcrChromeFilterResult(kept, regions.Count - kept.Count);
    }

    private static bool IsChrome(ScreenTextRegion region, int frameWidth, int strip, string normalisedTitle)
    {
        if (region.Y < 0 || region.Y + region.Height > strip)
        {
            return false; // not entirely inside the title strip
        }

        string text = region.Text.Trim();

        // Window buttons: up to two glyphs read together, or up to three when OCR separated them with spaces (minimise, maximise, close).
        string glyphs = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        int most = glyphs.Length < text.Length ? 3 : 2;
        if (glyphs.Length >= 1 && glyphs.Length <= most
            && glyphs.All(ControlGlyphs.Contains)
            && region.X >= frameWidth * RightThirdStart)
        {
            return true;
        }

        // An application icon read as punctuation (":" or "." in front of the title): short, and no letter, digit or Han character at all.
        if (text.Length is >= 1 and <= 3 && !text.Any(char.IsLetterOrDigit))
        {
            return true;
        }

        if (normalisedTitle.Length == 0)
        {
            return false;
        }

        string normalised = Normalise(text);
        if (!IsDistinctive(normalised))
        {
            return false;
        }

        // The title as read (possibly cut short or with an icon glyph read as a letter or two in front of or behind it), never a longer
        // text that merely contains the title: that would be content.
        return normalisedTitle.Contains(normalised, StringComparison.Ordinal)
            || (normalised.Contains(normalisedTitle, StringComparison.Ordinal) && normalised.Length - normalisedTitle.Length <= 2);
    }

    // Short fragments ("a", "-") would match any title; require three letters/digits, or two Han characters.
    private static bool IsDistinctive(string normalised) =>
        normalised.Length >= 3 || (normalised.Length == 2 && normalised.Any(IsHan));

    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static bool IsHan(char c) => c is >= '㐀' and <= '鿿';
}
