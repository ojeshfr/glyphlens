using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Extracts text that applications expose through accessibility APIs.
/// </summary>
/// <remarks>
/// Planned implementation: Microsoft UI Automation (GlyphLens.Windows, Phase 2).
/// Returned regions have <see cref="DetectionSource.Accessibility"/>.
/// </remarks>
public interface IAccessibilityTextSource
{
    Task<IReadOnlyList<ScreenTextRegion>> GetTextRegionsAsync(ScreenRectangle area, CancellationToken cancellationToken);
}
