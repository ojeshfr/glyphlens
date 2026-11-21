using System.Runtime.InteropServices;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GlyphLens.Windows.Ocr;

/// <summary>Encodes captured frames as PNG with the Windows imaging encoder (in memory only).</summary>
public sealed class PngFrameEncoder : IFrameImageEncoder
{
    public async Task<byte[]> EncodePngAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var bitmap = WindowsOcrEngine.CreateBitmap(frame);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

            var bytes = new byte[checked((int)stream.Size)];
            stream.Seek(0);
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)bytes.Length).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new OcrException(OcrErrorKind.Failed, "The captured frame could not be prepared for recognition.", ex);
        }
    }
}
