using GlyphLens.Core.Models;

namespace GlyphLens.Core.Capture;

/// <summary>
/// Thrown when a capture cannot be started or fails; carries a <see cref="CaptureError"/>.
/// </summary>
public sealed class CaptureException : Exception
{
    public CaptureException()
        : this(new CaptureError(CaptureErrorKind.Unexpected, "Capture failed."))
    {
    }

    public CaptureException(string message)
        : this(new CaptureError(CaptureErrorKind.Unexpected, message))
    {
    }

    public CaptureException(string message, Exception innerException)
        : this(new CaptureError(CaptureErrorKind.Unexpected, message), innerException)
    {
    }

    public CaptureException(CaptureError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public CaptureError Error { get; }
}
