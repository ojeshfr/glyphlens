using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Offers the local multilingual (Chinese-capable) OCR engine next to the built-in Windows OCR languages in one list, and
/// sends each recognition to the engine the user picked.
/// </summary>
/// <remarks>
/// Windows OCR can only read the languages Windows has installed (an English-only machine cannot read Chinese, and adding a
/// language needs administrator rights). The local engine reads Simplified and Traditional Chinese, English, Japanese and
/// digits in one model. Picking its entry (<see cref="LocalEngineTag"/>) selects it; any other entry (or "automatic",
/// <see langword="null"/>) selects Windows OCR with that language.
/// </remarks>
public sealed class OcrEngineSelector : IOcrEngine, IOcrLanguageSource
{
    /// <summary>The language tag of the local engine's entry.</summary>
    public const string LocalEngineTag = "local:multilingual";

    private readonly IOcrEngine _builtIn;
    private readonly IOcrLanguageSource _builtInLanguages;
    private readonly IOcrEngine _local;
    private readonly OcrLanguage _localEntry;
    private string? _selected;

    public OcrEngineSelector(IOcrEngine builtIn, IOcrLanguageSource builtInLanguages, IOcrEngine local, string localDisplayName)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(builtInLanguages);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDisplayName);

        _builtIn = builtIn;
        _builtInLanguages = builtInLanguages;
        _local = local;
        _localEntry = new OcrLanguage(LocalEngineTag, localDisplayName);
        _selected = builtInLanguages.SelectedLanguage;
    }

    /// <summary>The built-in languages followed by the local engine's entry.</summary>
    public IReadOnlyList<OcrLanguage> AvailableLanguages => [.. _builtInLanguages.AvailableLanguages, _localEntry];

    public string? SelectedLanguage
    {
        get => _selected;
        set
        {
            _selected = value;
            if (!IsLocal)
            {
                _builtInLanguages.SelectedLanguage = value;
            }
        }
    }

    /// <summary>Whether recognition currently goes to the local engine.</summary>
    public bool IsLocal => string.Equals(_selected, LocalEngineTag, StringComparison.Ordinal);

    public Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
        (IsLocal ? _local : _builtIn).RecognizeAsync(frame, cancellationToken);
}
