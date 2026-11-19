namespace GlyphLens.Core.Models;

/// <summary>
/// The mechanism that produced a <see cref="ScreenTextRegion"/>.
/// </summary>
public enum DetectionSource
{
    /// <summary>Text exposed by an application through Microsoft UI Automation.</summary>
    Accessibility = 1,

    /// <summary>Text recognised from captured screen pixels by an OCR engine.</summary>
    Ocr = 2,
}
