namespace GlyphLens.Core.Models;

/// <summary>
/// An immutable, reference-counted snapshot of captured pixels held in memory.
/// </summary>
/// <remarks>
/// <para>
/// The pixel buffer typically comes from a pool. The frame starts with one reference, owned by its
/// creator. Anyone who needs the pixels beyond the call that handed them a frame must call
/// <see cref="Retain"/> and later <see cref="Dispose"/>; the buffer is returned to its pool when the
/// last reference is released. <see cref="Pixels"/> must not be used after the holder's reference
/// is released, because the buffer will be reused for a later frame.
/// </para>
/// <para>
/// Frames contain potentially sensitive screen content. They are held in memory only and must never
/// be persisted or transmitted (see docs/SECURITY.md).
/// </para>
/// </remarks>
public sealed class CapturedFrame : IDisposable
{
    private readonly Action<byte[]>? _release;
    private byte[]? _buffer;
    private int _referenceCount = 1;

    /// <param name="metadata">Frame description.</param>
    /// <param name="buffer">
    /// Buffer holding at least <see cref="FrameMetadata.ByteLength"/> bytes of pixel data. It may be larger
    /// (pooled buffers usually are).
    /// </param>
    /// <param name="release">Called once, with <paramref name="buffer"/>, when the last reference is released.</param>
    public CapturedFrame(FrameMetadata metadata, byte[] buffer, Action<byte[]>? release = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length < metadata.ByteLength)
        {
            throw new ArgumentException("Pixel buffer is smaller than stride * height.", nameof(buffer));
        }

        Metadata = metadata;
        _buffer = buffer;
        _release = release;
    }

    public FrameMetadata Metadata { get; }

    /// <summary>Number of outstanding references; 0 once the frame has been released.</summary>
    public int ReferenceCount => Volatile.Read(ref _referenceCount);

    /// <summary>The pixel data (<c>Stride * Height</c> bytes). Valid only while the caller holds a reference.</summary>
    /// <exception cref="ObjectDisposedException">The last reference has been released.</exception>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            var buffer = Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(CapturedFrame));
            return new ReadOnlyMemory<byte>(buffer, 0, Metadata.ByteLength);
        }
    }

    /// <summary>Adds a reference and returns this frame. Pair every call with a <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The frame has already been released.</exception>
    public CapturedFrame Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _referenceCount);
            ObjectDisposedException.ThrowIf(current <= 0, this);
        }
        while (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) != current);

        return this;
    }

    /// <summary>Releases one reference. The pixel buffer is returned to its pool when none remain.</summary>
    public void Dispose()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _referenceCount, current - 1, current) != current);

        if (current == 1)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                _release?.Invoke(buffer);
            }
        }
    }
}
