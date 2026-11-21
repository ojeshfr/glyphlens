using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>Raised when a text-recognition pass finished (or its results were discarded).</summary>
public sealed class RegionsRecognisedEventArgs : EventArgs
{
    public RegionsRecognisedEventArgs(IReadOnlyList<ScreenTextRegion> regions, bool discarded = false)
    {
        ArgumentNullException.ThrowIfNull(regions);
        Regions = regions;
        Discarded = discarded;
    }

    /// <summary>The regions of the latest pass, each with its own identity and frame-relative bounds.</summary>
    public IReadOnlyList<ScreenTextRegion> Regions { get; }

    /// <summary>
    /// <see langword="true"/> when the results no longer describe what is being captured (capture stopped), so anything
    /// derived from them should be forgotten rather than kept.
    /// </summary>
    public bool Discarded { get; }
}

/// <summary>Something that produces recognised text regions, such as the OCR view model.</summary>
public interface IRecognisedTextSource
{
    /// <summary>Raised on the UI context after every recognition pass and when results are discarded.</summary>
    event EventHandler<RegionsRecognisedEventArgs>? RegionsRecognised;
}
