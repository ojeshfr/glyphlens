using System.Reflection;
using GlyphLens.Core.Contracts;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>: product information and the capture diagnostic.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    public MainViewModel(CaptureViewModel capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Capture = capture;
        VersionInfo = $"App version {GetAppVersion()} · Worker protocol {ProtocolVersion.Current}";
    }

    public CaptureViewModel Capture { get; }

    public string ProductName { get; } = "GlyphLens";

    public string Tagline { get; } = "Privacy-first, offline screen translation for Windows.";

    public string FoundationStatus { get; } = "Phase 1: screen capture diagnostics.";

    public string FeatureStatus { get; } =
        "This build captures a display or window and shows a live preview to verify capture. OCR, accessibility "
        + "text extraction and translation are not implemented yet. Frames stay in memory: nothing is saved or sent anywhere.";

    public string VersionInfo { get; }

    public void Dispose() => Capture.Dispose();

    private static string GetAppVersion() =>
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";
}
