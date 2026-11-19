using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Recognises visually rendered text in captured pixels.
/// </summary>
/// <remarks>
/// Planned implementation: PaddleOCR inside the local Python worker, reached over IPC (Phase 2).
/// Returned regions have <see cref="DetectionSource.Ocr"/>.
/// </remarks>
public interface IOcrEngine
{
    Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}
