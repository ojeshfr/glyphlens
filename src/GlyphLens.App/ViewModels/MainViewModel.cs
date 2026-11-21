using System.Collections.ObjectModel;
using System.Reflection;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Contracts;

namespace GlyphLens.App.ViewModels;

/// <summary>The pages of the main (management) window.</summary>
public enum MainPage
{
    /// <summary>Readiness checklist and the overlay controls.</summary>
    Overview,

    /// <summary>Capture target, text recognition and the translation results.</summary>
    Capture,

    /// <summary>Translation models, the OCR models and the worker.</summary>
    Models,

    /// <summary>Overlay appearance, shortcuts, privacy and version.</summary>
    Settings,
}

/// <summary>One entry of the navigation list. Its text form is its label (what a screen reader announces).</summary>
public sealed record NavItem(MainPage Page, string Label, string AutomationId)
{
    /// <summary>The page's icon: a glyph of the Segoe Fluent Icons font (home, screen, library, gear).</summary>
    public string Glyph => Page switch
    {
        MainPage.Overview => "",
        MainPage.Capture => "",
        MainPage.Models => "",
        MainPage.Settings => "",
        _ => string.Empty,
    };

    public override string ToString() => Label;
}

/// <summary>
/// View model for <see cref="MainWindow"/>, the management window: it hosts the pages (Overview, Capture, Models, Settings) and exposes
/// the view models behind them. The translations themselves are shown in the floating overlay.
/// </summary>
/// <remarks>History is not a page: nothing is persisted, so there is no history to show.</remarks>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private NavItem _selectedNavItem;

    public MainViewModel(
        CaptureViewModel capture,
        OcrViewModel ocr,
        TranslationViewModel translation,
        OverlayViewModel overlay,
        WorkerViewModel worker,
        ReadinessViewModel? readiness = null,
        AppearanceViewModel? appearance = null,
        UpdatesViewModel? updates = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(worker);
        Capture = capture;
        Ocr = ocr;
        Translation = translation;
        Overlay = overlay;
        Worker = worker;
        Readiness = readiness;
        Appearance = appearance;
        Updates = updates;
        VersionInfo = $"App version {GetAppVersion()} · Worker protocol {ProtocolVersion.Current}";

        Pages = new ReadOnlyCollection<NavItem>(
        [
            new NavItem(MainPage.Overview, "Overview", "NavOverview"),
            new NavItem(MainPage.Capture, "Capture", "NavCapture"),
            new NavItem(MainPage.Models, "Models", "NavModels"),
            new NavItem(MainPage.Settings, "Settings", "NavSettings"),
        ]);
        _selectedNavItem = Pages[0];
        OpenSettingsCommand = new RelayCommand<object>(_ => Navigate(MainPage.Settings));
    }

    /// <summary>Opens the Settings page (from the update banner on the Overview).</summary>
    public RelayCommand<object> OpenSettingsCommand { get; }

    public CaptureViewModel Capture { get; }

    public OcrViewModel Ocr { get; }

    public TranslationViewModel Translation { get; }

    /// <summary>The floating translation overlay: the primary place translations are shown. This window configures it.</summary>
    public OverlayViewModel Overlay { get; }

    public WorkerViewModel Worker { get; }

    /// <summary>The first-run checklist, or <see langword="null"/> when not composed (tests).</summary>
    public ReadinessViewModel? Readiness { get; }

    /// <summary>The appearance setting (System / Light / Dark), or <see langword="null"/> when not composed (tests).</summary>
    public AppearanceViewModel? Appearance { get; }

    public bool HasAppearance => Appearance is not null;

    public bool HasUpdates => Updates is not null;

    /// <summary>The update checker, or <see langword="null"/> when not composed (tests).</summary>
    public UpdatesViewModel? Updates { get; }

    public IReadOnlyList<NavItem> Pages { get; }

    /// <summary>The navigation entry that is selected; selecting one shows its page.</summary>
    public NavItem SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (value is null || !SetProperty(ref _selectedNavItem, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedPage));
            OnPropertyChanged(nameof(IsOverviewPage));
            OnPropertyChanged(nameof(IsCapturePage));
            OnPropertyChanged(nameof(IsModelsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    public MainPage SelectedPage => _selectedNavItem.Page;

    public bool IsOverviewPage => SelectedPage == MainPage.Overview;

    public bool IsCapturePage => SelectedPage == MainPage.Capture;

    public bool IsModelsPage => SelectedPage == MainPage.Models;

    public bool IsSettingsPage => SelectedPage == MainPage.Settings;

    /// <summary>Shows a page.</summary>
    public void Navigate(MainPage page) => SelectedNavItem = Pages.First(p => p.Page == page);

    public string ProductName { get; } = "GlyphLens";

    public string Tagline { get; } = "Offline screen translation for Windows.";

    public string FoundationStatus { get; } = "Everything runs on this computer.";

    public string FeatureStatus { get; } =
        "GlyphLens captures a display or window, reads its text (Windows OCR, or the local Chinese OCR through the Python worker), "
        + "identifies the language and translates it to English with a local Argos model, shown in a floating overlay. Nothing is "
        + "uploaded, models are never downloaded automatically (only when you click Install everything), and frames and recognised text stay in memory. Without an installed "
        + "model the app says so instead of guessing a translation. Text extraction through accessibility APIs and a history of "
        + "translations are not implemented.";

    public string PrivacyText { get; } =
        "Screen content never leaves this computer: GlyphLens sends no screenshots, recognised text or translations anywhere. It uses "
        + "the network only for two things. Update checks: when \"Check for updates automatically\" is on (the default), it asks the "
        + "GlyphLens update server about once a day whether a new version exists, sending nothing but that request; an update is "
        + "downloaded only when you choose to. Install everything: only when you click it on the Overview page and confirm, it downloads "
        + "Python, the OCR and translation packages and the models from their publishers, each checked against a pinned hash. After "
        + "that, translation works offline. The translation and OCR run in a local worker process that is forbidden to open network "
        + "connections, and the Overview checks the connections of GlyphLens' own processes on request. Screenshots and recognised text "
        + "are never written to disk or logged; GlyphLens remembers only the overlay's position, size, opacity, text size and pin "
        + "setting, the appearance, the update settings and whether it created the desktop shortcut.";

    public string VersionInfo { get; }

    public void Dispose()
    {
        Readiness?.Dispose();
        Appearance?.Dispose();
        Updates?.Dispose();
        Overlay.Dispose();
        Translation.Dispose();
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
