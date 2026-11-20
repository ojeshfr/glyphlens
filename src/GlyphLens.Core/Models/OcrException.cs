namespace GlyphLens.Core.Models;

/// <summary>Category of an OCR failure.</summary>
public enum OcrErrorKind
{
    /// <summary>The OCR engine is not available on this system (for example no OCR language pack is installed).</summary>
    EngineUnavailable = 1,

    /// <summary>The requested recognition language is not installed.</summary>
    LanguageNotInstalled,

    /// <summary>The frame is larger than the engine supports.</summary>
    FrameTooLarge,

    /// <summary>Recognition failed for another reason.</summary>
    Failed,
}

/// <summary>
/// An OCR failure. The message is safe to show to the user and never contains recognised or captured text.
/// </summary>
public sealed class OcrException : Exception
{
    public OcrException()
        : this(OcrErrorKind.Failed, "Text recognition failed.")
    {
    }

    public OcrException(string message)
        : this(OcrErrorKind.Failed, message)
    {
    }

    public OcrException(string message, Exception innerException)
        : this(OcrErrorKind.Failed, message, innerException)
    {
    }

    public OcrException(OcrErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public OcrErrorKind Kind { get; }
}
