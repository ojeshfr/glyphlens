using System.Reflection;
using GlyphLens.Core.Contracts;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>: product information, the capture diagnostic and the worker diagnostics.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    public MainViewModel(CaptureViewModel capture, WorkerViewModel worker)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(worker);
        Capture = capture;
        Worker = worker;
        VersionInfo = $"App version {GetAppVersion()} · Worker protocol {ProtocolVersion.Current}";
    }

    public CaptureViewModel Capture { get; }

    public WorkerViewModel Worker { get; }

    public string ProductName { get; } = "GlyphLens";

    public string Tagline { get; } = "Privacy-first, offline screen translation for Windows.";

    public string FoundationStatus { get; } = "Phase 2a: worker communication.";

    public string FeatureStatus { get; } =
        "Screen capture shows a live preview. The Python worker can be started, checked and stopped over a secure local pipe, "
        + "but OCR, language detection, accessibility text extraction and translation are not implemented yet. "
        + "Frames stay in memory: nothing is saved or sent anywhere.";

    public string VersionInfo { get; }

    public void Dispose()
    {
        Capture.Dispose();
        Worker.Dispose();
    }

    private static string GetAppVersion() =>
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";
}
