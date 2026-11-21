using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Encodes a captured frame as an image file in memory, to hand it to an out-of-process engine.
/// </summary>
/// <remarks>
/// Frames are screen content: the encoded bytes are never written to disk and are only sent over the authenticated
/// local pipe. The caller keeps ownership of <c>frame</c>.
/// </remarks>
public interface IFrameImageEncoder
{
    /// <summary>Returns the frame as PNG bytes.</summary>
    /// <exception cref="OcrException">The frame cannot be encoded.</exception>
    Task<byte[]> EncodePngAsync(CapturedFrame frame, CancellationToken cancellationToken);
}
