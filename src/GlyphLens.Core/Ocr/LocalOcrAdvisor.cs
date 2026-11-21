using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Ocr;

/// <summary>What the worker reports about the local Chinese-capable OCR engine.</summary>
public enum LocalOcrState
{
    /// <summary>Nothing is known yet: the worker is not running, so it has not been asked.</summary>
    Unknown,

    /// <summary>The runtime and the PP-OCRv5 model files are present.</summary>
    Ready,

    /// <summary>The runtime is present but the model files are not.</summary>
    ModelsMissing,

    /// <summary>The OCR runtime packages are not installed in the worker's Python environment (or the worker has no OCR).</summary>
    RuntimeMissing,
}

/// <summary>How prominently the advice should be shown.</summary>
public enum OcrAdviceSeverity
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>An explanation of something GlyphLens did or can do.</summary>
    Info,

    /// <summary>Chinese text cannot be read with the current setup.</summary>
    Problem,
}

/// <summary>
/// What to do about the OCR engine for the current language situation: whether to switch to the local engine, and what to tell
/// the user.
/// </summary>
/// <param name="SwitchToLocal">Select the local Chinese-capable engine (only ever true when it is ready and the user has not chosen).</param>
/// <param name="Severity">How prominently to show <paramref name="Message"/>.</param>
/// <param name="Message">One or two plain sentences, or <see langword="null"/> when there is nothing to say.</param>
/// <param name="OffersModelsFolder">Whether the remedy is to put model files into the OCR models folder (so the user can be offered that folder).</param>
public sealed record OcrAdvice(bool SwitchToLocal, OcrAdviceSeverity Severity, string? Message, bool OffersModelsFolder = false)
{
    /// <summary>No switch and no message.</summary>
    public static OcrAdvice Nothing { get; } = new(false, OcrAdviceSeverity.None, null);
}

/// <summary>
/// Decides which OCR engine fits a Chinese-first workflow and explains it. Pure: it only looks at its arguments.
/// </summary>
/// <remarks>
/// <para>
/// Windows OCR can read only the languages Windows has installed, and on a machine without a Chinese OCR language it cannot read
/// Chinese at all. The local engine (PP-OCRv5 through the worker) can. The rules, in order:
/// </para>
/// <list type="bullet">
/// <item>Outside a Chinese situation (the source is neither Simplified nor Traditional Chinese, and "detect automatically" with no
/// Chinese translation model installed is not a Chinese situation) nothing changes.</item>
/// <item>The user's own choice of OCR engine is never overridden: it is only explained when it cannot read Chinese.</item>
/// <item>Otherwise the local engine is selected when it is ready. When it is not, nothing is switched and the user is told why and
/// what is missing; an English-only engine is never silently relied on for Chinese.</item>
/// </list>
/// </remarks>
public static class LocalOcrAdvisor
{
    /// <summary>The Simplified Chinese language code.</summary>
    public const string SimplifiedChinese = "zh";

    /// <summary>The Traditional Chinese language code.</summary>
    public const string TraditionalChinese = "zt";

    private const string UsingLocalBecauseChinese = "Reading with the local Chinese OCR, because Chinese is the language to translate.";

    /// <summary>Maps the worker's health report to a <see cref="LocalOcrState"/>.</summary>
    public static LocalOcrState Classify(WorkerHealth? health) =>
        health is null
            ? LocalOcrState.Unknown
            : health.Ocr switch
            {
                WorkerComponentState.Ready or WorkerComponentState.Installed => LocalOcrState.Ready,
                WorkerComponentState.NotInstalled => LocalOcrState.ModelsMissing,
                WorkerComponentState.Unavailable or WorkerComponentState.NotImplemented => LocalOcrState.RuntimeMissing,
                _ => LocalOcrState.Unknown,
            };

    /// <summary>Whether <paramref name="code"/> is Simplified or Traditional Chinese.</summary>
    public static bool IsChinese(string? code) =>
        string.Equals(code, SimplifiedChinese, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, TraditionalChinese, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the situation is a Chinese one: Chinese chosen as the source, or "detect automatically" while a Chinese
    /// translation model is installed (the user installed it to read Chinese).
    /// </summary>
    /// <param name="sourceLanguage">The chosen translation source, or <see langword="null"/> for "detect automatically".</param>
    /// <param name="installedSourceLanguages">The source languages of the installed translation models.</param>
    public static bool IsChineseSituation(string? sourceLanguage, IEnumerable<string?> installedSourceLanguages)
    {
        ArgumentNullException.ThrowIfNull(installedSourceLanguages);
        return sourceLanguage is null ? installedSourceLanguages.Any(IsChinese) : IsChinese(sourceLanguage);
    }

    /// <summary>Whether the Windows OCR choice <paramref name="selectedTag"/> can read Chinese.</summary>
    /// <param name="selectedTag">The chosen Windows OCR language tag, or <see langword="null"/> for "automatic".</param>
    /// <param name="windowsLanguages">The Windows OCR languages installed (the local engine's entry excluded).</param>
    public static bool WindowsCanReadChinese(string? selectedTag, IEnumerable<OcrLanguage> windowsLanguages)
    {
        ArgumentNullException.ThrowIfNull(windowsLanguages);
        return selectedTag is null
            ? windowsLanguages.Any(l => IsChineseTag(l.Tag))
            : IsChineseTag(selectedTag);
    }

    /// <summary>Computes the advice for the current situation.</summary>
    /// <param name="chineseSituation">See <see cref="IsChineseSituation"/>.</param>
    /// <param name="selectedTag">The OCR language entry now selected (<see cref="OcrEngineSelector.LocalEngineTag"/>, a Windows tag, or <see langword="null"/> for automatic).</param>
    /// <param name="userChoseOcr">Whether the user picked that entry themselves (as opposed to the default or an automatic switch).</param>
    /// <param name="local">What is known about the local engine.</param>
    /// <param name="windowsLanguages">The Windows OCR languages installed (the local engine's entry excluded).</param>
    public static OcrAdvice Advise(
        bool chineseSituation,
        string? selectedTag,
        bool userChoseOcr,
        LocalOcrState local,
        IReadOnlyCollection<OcrLanguage> windowsLanguages)
    {
        ArgumentNullException.ThrowIfNull(windowsLanguages);

        if (!chineseSituation)
        {
            return OcrAdvice.Nothing;
        }

        bool localSelected = string.Equals(selectedTag, OcrEngineSelector.LocalEngineTag, StringComparison.Ordinal);
        bool currentReadsChinese = localSelected || WindowsCanReadChinese(selectedTag, windowsLanguages);

        switch (local)
        {
            case LocalOcrState.Ready:
                if (localSelected)
                {
                    // A deliberate choice needs no comment; the default (or an automatic switch) is explained.
                    return userChoseOcr ? OcrAdvice.Nothing : new OcrAdvice(false, OcrAdviceSeverity.Info, UsingLocalBecauseChinese);
                }

                if (!userChoseOcr)
                {
                    return new OcrAdvice(true, OcrAdviceSeverity.Info, UsingLocalBecauseChinese);
                }

                return currentReadsChinese
                    ? OcrAdvice.Nothing
                    : new OcrAdvice(false, OcrAdviceSeverity.Problem, "The OCR language you chose cannot read Chinese. Choose “Chinese + English (local OCR worker)” in the OCR language list.");

            case LocalOcrState.ModelsMissing:
                return new OcrAdvice(
                    false,
                    currentReadsChinese ? OcrAdviceSeverity.Info : OcrAdviceSeverity.Problem,
                    "The local Chinese OCR models are not installed: put the PP-OCRv5 detection and recognition folders in the OCR models folder (see “Open OCR folder”)."
                    + (currentReadsChinese ? string.Empty : " Windows OCR cannot read Chinese on this PC, so Chinese text will not be recognised until then."),
                    OffersModelsFolder: true);

            case LocalOcrState.RuntimeMissing:
                return new OcrAdvice(
                    false,
                    currentReadsChinese ? OcrAdviceSeverity.Info : OcrAdviceSeverity.Problem,
                    "The local Chinese OCR is not available: the worker's Python environment lacks the OCR packages (PaddleOCR and ONNX Runtime)."
                    + (currentReadsChinese ? string.Empty : " Windows OCR cannot read Chinese on this PC, so Chinese text will not be recognised."));

            default:
                return currentReadsChinese
                    ? OcrAdvice.Nothing
                    : new OcrAdvice(false, OcrAdviceSeverity.Info, "Windows OCR cannot read Chinese on this PC. Turn translation on so GlyphLens can check the local Chinese OCR.");
        }
    }

    private static bool IsChineseTag(string tag) => tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
