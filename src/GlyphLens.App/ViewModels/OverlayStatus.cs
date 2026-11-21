using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>The state the overlay reports about itself in its header.</summary>
public enum OverlayStateKind
{
    /// <summary>Translation is turned off.</summary>
    Off,

    /// <summary>Translation is paused by the user.</summary>
    Paused,

    /// <summary>The worker is starting.</summary>
    Starting,

    /// <summary>The worker is not running or not answering, so nothing can be recognised or translated.</summary>
    WorkerUnavailable,

    /// <summary>No screen is being captured, so nothing new will appear.</summary>
    CaptureStopped,

    /// <summary>The text was read but no installed model translates its language.</summary>
    ModelMissing,

    /// <summary>Some lines failed to translate.</summary>
    Error,

    /// <summary>The language of the text could not be determined, so it was not translated.</summary>
    Uncertain,

    /// <summary>The screen was read and contains no text.</summary>
    NoText,

    /// <summary>Everything needed is running; text is read when asked.</summary>
    Ready,

    /// <summary>Everything needed is running and the screen is being read continuously.</summary>
    Live,
}

/// <summary>How much attention a status deserves (drives the colour of its indicator).</summary>
public enum StatusLevel
{
    Neutral,
    Ok,
    Attention,
    Problem,
}

/// <summary>A state with the words shown for it.</summary>
public sealed record OverlayStatus(OverlayStateKind Kind, string Text, StatusLevel Level);

/// <summary>What the latest translation pass found, in counts (never the text itself).</summary>
public sealed record PassSummary(
    int Regions,
    int Translated,
    int ModelMissing,
    int Uncertain,
    int Failed,
    int WorkerUnavailable,
    int EngineUnavailable,
    int NoText)
{
    /// <summary>Counts the outcomes of a pass.</summary>
    public static PassSummary From(IEnumerable<Core.Translation.RegionTranslation> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var list = regions.ToList();
        return new PassSummary(
            list.Count,
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.Translated),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.ModelMissing),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.LanguageUncertain),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.Failed),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.WorkerUnavailable),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.EngineUnavailable),
            list.Count(r => r.Status == Core.Translation.RegionTranslationStatus.NoText));
    }
}

/// <summary>Everything the overlay's state is derived from.</summary>
/// <param name="TranslationEnabled">Whether translation is turned on.</param>
/// <param name="Paused">Whether it is paused.</param>
/// <param name="Worker">The worker's lifecycle state.</param>
/// <param name="Capturing">Whether a screen is being captured, or <see langword="null"/> when that is not known.</param>
/// <param name="Live">Whether the screen is being read continuously.</param>
/// <param name="LastPass">What the latest pass found, or <see langword="null"/> before the first one.</param>
public sealed record OverlayStatusInput(bool TranslationEnabled, bool Paused, WorkerStatus Worker, bool? Capturing, bool Live, PassSummary? LastPass);

/// <summary>
/// Turns the facts about capture, worker and the latest pass into the one state shown in the overlay's header. Pure.
/// </summary>
/// <remarks>
/// <para>
/// "Ready" and "Live" (the only states with a green indicator) are reported only when the worker answers, a screen is captured and
/// the latest pass had no problem: a running process alone never shows green. The first matching rule below wins.
/// </para>
/// </remarks>
public static class OverlayStatusEvaluator
{
    public static OverlayStatus Evaluate(OverlayStatusInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.TranslationEnabled)
        {
            return new OverlayStatus(OverlayStateKind.Off, "Off", StatusLevel.Neutral);
        }

        if (input.Paused)
        {
            return new OverlayStatus(OverlayStateKind.Paused, "Paused", StatusLevel.Neutral);
        }

        if (input.Worker == WorkerStatus.Starting)
        {
            return new OverlayStatus(OverlayStateKind.Starting, "Starting…", StatusLevel.Neutral);
        }

        var pass = input.LastPass;
        if (input.Worker is WorkerStatus.Error or WorkerStatus.Unavailable or WorkerStatus.Stopped
            || pass is { WorkerUnavailable: > 0 } or { EngineUnavailable: > 0 })
        {
            return new OverlayStatus(OverlayStateKind.WorkerUnavailable, "Worker unavailable", StatusLevel.Problem);
        }

        if (input.Capturing == false)
        {
            return new OverlayStatus(OverlayStateKind.CaptureStopped, "Capture stopped", StatusLevel.Attention);
        }

        if (pass is { ModelMissing: > 0 })
        {
            return new OverlayStatus(OverlayStateKind.ModelMissing, "Model missing", StatusLevel.Attention);
        }

        if (pass is { Failed: > 0 })
        {
            return new OverlayStatus(OverlayStateKind.Error, "Error", StatusLevel.Problem);
        }

        if (pass is { Uncertain: > 0, Translated: 0 })
        {
            return new OverlayStatus(OverlayStateKind.Uncertain, "Uncertain language", StatusLevel.Attention);
        }

        if (pass is not null && (pass.Regions == 0 || pass.NoText == pass.Regions))
        {
            return new OverlayStatus(OverlayStateKind.NoText, "No text found", StatusLevel.Neutral);
        }

        return input.Live
            ? new OverlayStatus(OverlayStateKind.Live, "Live", StatusLevel.Ok)
            : new OverlayStatus(OverlayStateKind.Ready, "Ready", StatusLevel.Ok);
    }
}
