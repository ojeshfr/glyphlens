using System.Reflection;
using GlyphLens.Core.Contracts;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>: product information, the capture diagnostic and the worker diagnostics.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    public MainViewModel(CaptureViewModel capture, OcrViewModel ocr, WorkerViewModel worker)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(worker);
        Capture = capture;
        Ocr = ocr;
        Worker = worker;
        VersionInfo = $"App version {GetAppVersion()} · Worker protocol {ProtocolVersion.Current}";
    }

    public CaptureViewModel Capture { get; }

    public OcrViewModel Ocr { get; }

    public WorkerViewModel Worker { get; }

    public string ProductName { get; } = "GlyphLens";

    public string Tagline { get; } = "Privacy-first, offline screen translation for Windows.";

    public string FoundationStatus { get; } = "Phase 2b: reading text from the screen.";

    public string FeatureStatus { get; } =
        "Capture a display or window, then read its text with Windows OCR (offline). Translation, language detection and "
        + "accessibility text extraction are not implemented yet, and the Python worker has no engines yet. "
        + "Frames and recognised text stay in memory: nothing is saved or sent anywhere.";

    public string VersionInfo { get; }

    public void Dispose()
    {
        Ocr.Dispose();
        Capture.Dispose();
        Worker.Dispose();
    }

    private static string GetAppVersion() =>
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";
}
