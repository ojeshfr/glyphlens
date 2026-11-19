using System.Buffers;
using GlyphLens.Core.Models;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using CorePixelFormat = GlyphLens.Core.Models.PixelFormat;
using D3D11MapFlags = Vortice.Direct3D11.MapFlags;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Converts a GPU capture frame into a CPU-memory <see cref="CapturedFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each frame is copied on the GPU into a reusable staging texture, mapped, and copied row by row into a
/// pooled byte array with the GPU's row padding removed (tight stride = width * 4). The staging texture is
/// recreated only when the frame size changes, and the pixel arrays come from <see cref="ArrayPool{T}"/>, so
/// steady-state capture performs no per-frame managed allocation beyond the small frame object.
/// </para>
/// <para>Not thread-safe; the session manager serialises calls.</para>
/// </remarks>
internal sealed class FrameProcessor : IDisposable
{
    private readonly Direct3DDeviceManager _device;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private bool _disposed;

    internal FrameProcessor(Direct3DDeviceManager device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>
    /// Reads <paramref name="frame"/> back to CPU memory.
    /// </summary>
    /// <returns>The frame (one reference, owned by the caller), or null when the frame has no visible content.</returns>
    internal CapturedFrame? Process(
        Direct3D11CaptureFrame frame,
        string targetId,
        long sequence,
        ScreenRectangle? screenBounds,
        DateTimeOffset timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        var contentWidth = frame.ContentSize.Width;
        var contentHeight = frame.ContentSize.Height;
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return null; // e.g. a minimised window: nothing to show
        }

        // Close the per-frame WinRT surface wrapper promptly (about 30 per second) instead of leaving it to the GC.
        using var surface = frame.Surface;
        using var texture = WinRtInterop.GetTexture(surface);
        var description = texture.Description;
        if (description.Format != Format.B8G8R8A8_UNorm)
        {
            throw new NotSupportedException($"Unexpected capture surface format {description.Format}.");
        }

        var surfaceWidth = checked((int)description.Width);
        var surfaceHeight = checked((int)description.Height);
        var width = Math.Min(contentWidth, surfaceWidth);
        var height = Math.Min(contentHeight, surfaceHeight);
        var staging = EnsureStagingTexture(surfaceWidth, surfaceHeight);

        var stride = checked(width * FrameMetadata.BytesPerPixel);
        var length = checked(stride * height);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            _device.Context.CopyResource(staging, texture);
            var mapped = _device.Context.Map(staging, 0u, MapMode.Read, D3D11MapFlags.None);
            try
            {
                CopyRows(mapped, buffer, stride, height);
            }
            finally
            {
                _device.Context.Unmap(staging, 0u);
            }

            var metadata = new FrameMetadata(targetId, width, height, stride, CorePixelFormat.Bgra8, timestamp, sequence, screenBounds);
            return new CapturedFrame(metadata, buffer, static returned => ArrayPool<byte>.Shared.Return(returned));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes from a source whose rows are
    /// <paramref name="sourcePitch"/> bytes apart into a tightly packed destination.
    /// </summary>
    internal static void CopyRows(ReadOnlySpan<byte> source, int sourcePitch, Span<byte> destination, int rowBytes, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourcePitch, rowBytes);
        if (rows <= 0)
        {
            return;
        }

        var requiredSource = (long)(rows - 1) * sourcePitch + rowBytes;
        if (source.Length < requiredSource)
        {
            throw new ArgumentException("Source is smaller than the described rows.", nameof(source));
        }

        if (destination.Length < (long)rowBytes * rows)
        {
            throw new ArgumentException("Destination is smaller than the packed rows.", nameof(destination));
        }

        if (sourcePitch == rowBytes)
        {
            source[..(rowBytes * rows)].CopyTo(destination);
            return;
        }

        for (var row = 0; row < rows; row++)
        {
            source.Slice(row * sourcePitch, rowBytes).CopyTo(destination.Slice(row * rowBytes, rowBytes));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _staging?.Dispose();
        _staging = null;
    }

    private static unsafe void CopyRows(MappedSubresource mapped, byte[] destination, int rowBytes, int rows)
    {
        var pitch = checked((int)mapped.RowPitch);
        var sourceLength = checked(((rows - 1) * pitch) + rowBytes);
        var source = new ReadOnlySpan<byte>((void*)mapped.DataPointer, sourceLength);
        CopyRows(source, pitch, destination, rowBytes, rows);
    }

    private ID3D11Texture2D EnsureStagingTexture(int width, int height)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height)
        {
            return _staging;
        }

        _staging?.Dispose();
        _staging = null;

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        _staging = _device.Device.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        return _staging;
    }
}
