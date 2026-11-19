using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// A frame returned by <see cref="IFrameSource"/>: a retained frame plus the version it was published as.
/// </summary>
/// <remarks>The receiver owns one reference to <see cref="Frame"/> and must dispose it.</remarks>
public readonly record struct LatestFrame(CapturedFrame Frame, long Version);

/// <summary>
/// Publishes the most recent captured frame to consumers (preview, and later OCR) without queuing.
/// </summary>
/// <remarks>
/// <para>
/// Only the latest frame is kept. If a consumer is slower than the capture rate, intermediate frames
/// are dropped and disposed, so a slow consumer can never cause an unbounded backlog or unbounded memory.
/// </para>
/// <para>
/// <see cref="Version"/> increases by one for every published frame and keeps increasing across capture
/// sessions, so a consumer can wait for "anything newer than what I last processed".
/// </para>
/// </remarks>
public interface IFrameSource
{
    /// <summary>Version of the most recently published frame; 0 before the first frame.</summary>
    long Version { get; }

    /// <summary>Metadata of the latest frame, or <see langword="null"/> when there is none (no capture yet, or cleared).</summary>
    FrameMetadata? LatestMetadata { get; }

    /// <summary>Returns the latest frame with an added reference, or <see langword="null"/> when there is none.</summary>
    LatestFrame? TryAcquireLatest();

    /// <summary>
    /// Waits until a frame newer than <paramref name="afterVersion"/> exists and returns it with an added reference.
    /// Returns <see langword="null"/> when the source has been disposed.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    ValueTask<LatestFrame?> WaitForNewerAsync(long afterVersion, CancellationToken cancellationToken);
}
