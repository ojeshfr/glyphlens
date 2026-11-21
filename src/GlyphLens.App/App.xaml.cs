using System.Diagnostics;
using System.IO;
using System.Windows;
using GlyphLens.App.Overlay;
using GlyphLens.App.Services;
using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Ocr;
using GlyphLens.Core.Translation;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Infrastructure.Ocr;
using GlyphLens.Infrastructure.Translation;
using GlyphLens.Infrastructure.Updates;
using GlyphLens.Windows.Capture;
using GlyphLens.Windows.Display;
using GlyphLens.Windows.Ocr;
using GlyphLens.Windows.Platform;
using GlyphLens.Windows.Worker;

namespace GlyphLens.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// Services are composed here; windows and view models receive them through constructors. Capture is stopped and the
/// worker is shut down when the application exits, so neither can outlive the app. The worker is never started
/// at launch: the user starts it from the diagnostics panel, or by turning translation on. Translation runs entirely
/// through that local worker. The only network requests are the updater's (<see cref="UpdateService"/>): the signed update feed and,
/// when the user asks, an installer; never any screen content.
/// </remarks>
public partial class App : Application, IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private WindowsGraphicsCaptureService? _captureService;
    private WindowsOcrEngine? _ocrEngine;
    private PythonWorkerProcessLauncher? _workerLauncher;
    private WorkerProcessManager? _workerManager;
    private WorkerOcrEngine? _workerOcrEngine;
    private WorkerTranslationEngine? _translationEngine;
    private TranslationPipeline? _translationPipeline;
    private MainViewModel? _mainViewModel;
    private OcrEngineAdvisor? _ocrAdvisor;
    private IncrementalOcrEngine? _incrementalOcr;
    private OverlayWindow? _overlayWindow;
    private GlobalHotkeyService? _hotkeys;
    private WindowsSystemTheme? _systemTheme;
    private ThemeService? _themeService;
    private HttpUpdateClient? _updateClient;
    private UpdateService? _updateService;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateService?.Dispose();
        _updateClient?.Dispose();
        _themeService?.Dispose();
        _systemTheme?.Dispose();
        _hotkeys?.Dispose();
        _ocrAdvisor?.Dispose();
        _incrementalOcr?.Dispose();
        _overlayWindow?.Close();
        _mainViewModel?.Dispose();
        _translationPipeline?.Dispose();
        _translationEngine?.Dispose();
        _workerOcrEngine?.Dispose();
        _ocrEngine?.Dispose();

        // Bounded, so a stuck driver call or worker can never keep the application from exiting.
        var shutdown = Task.WhenAll(
            _captureService?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _workerManager?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        _ = shutdown.Wait(ShutdownTimeout);

        // Closing the job object ends any worker that is somehow still running.
        _workerLauncher?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The overlay may outlive the main window, so the application ends explicitly (see EvaluateShutdown).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Appearance first, so every window opens in the right palette (light, dark, or Windows' choice).
        var preferences = new AppPreferencesStore();
        _systemTheme = new WindowsSystemTheme();
        _themeService = new ThemeService(new WpfThemeApplier(), _systemTheme, preferences.Load().Theme);

        _captureService = new WindowsGraphicsCaptureService();
        var captureViewModel = new CaptureViewModel(_captureService, new CaptureTargetProvider());

        _workerLauncher = new PythonWorkerProcessLauncher();
        _workerManager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), _workerLauncher, log: new TraceWorkerLog());
        var workerViewModel = new WorkerViewModel(_workerManager);

        // Windows OCR reads only the languages Windows has installed; the worker's local engine reads Chinese and English.
        _ocrEngine = new WindowsOcrEngine();
        _workerOcrEngine = new WorkerOcrEngine(_workerManager, new PngFrameEncoder());
        var ocrSelector = new OcrEngineSelector(_ocrEngine, _ocrEngine, _workerOcrEngine, "Chinese + English (local OCR worker)");

        // A live feed of nearly identical frames is not read from scratch each time: unchanged screens are skipped and a changed part
        // is read on its own (see IncrementalOcrEngine). GLYPHLENS_FULL_OCR=1 turns that off and reads every frame completely.
        IOcrEngine recognition = ocrSelector;
        if (Environment.GetEnvironmentVariable("GLYPHLENS_FULL_OCR") != "1")
        {
            _incrementalOcr = new IncrementalOcrEngine(ocrSelector, _captureService, () => ocrSelector.SelectedLanguage);
            recognition = _incrementalOcr;
        }

        var ocrViewModel = new OcrViewModel(recognition, ocrSelector, _captureService, captureViewModel.Overlay, text => Clipboard.SetText(text));

        var detector = new WorkerLanguageDetector(_workerManager);
        var catalog = new WorkerModelCatalog(_workerManager);
        _translationEngine = new WorkerTranslationEngine(_workerManager);
        _translationPipeline = new TranslationPipeline(detector, catalog, _translationEngine, new LruTranslationCache(), new TranslationPipelineOptions { Grouper = new ParagraphRegionGrouper() });
        var translationViewModel = new TranslationViewModel(
            _translationPipeline,
            catalog,
            _workerManager,
            ocrViewModel,
            new WpfModelFilePicker(),
            text => Clipboard.SetText(text));

        // Chinese first: select the local Chinese OCR when Chinese is what is translated (never over the user's own choice), or say what is missing.
        _ocrAdvisor = new OcrEngineAdvisor(ocrViewModel, translationViewModel, _workerManager, SynchronizationContext.Current);
        ocrViewModel.OpenOcrFolderRequested += (_, _) => OpenOcrFolder(ocrViewModel);

        var overlayViewModel = new OverlayViewModel(translationViewModel, text => Clipboard.SetText(text), ocrViewModel);
        // The first-run checklist; its offline check reads which connections GlyphLens' own processes have (Windows TCP/UDP tables).
        var readiness = new ReadinessViewModel(translationViewModel, ocrViewModel, captureViewModel, _workerManager, new WindowsConnectionProbe(), SynchronizationContext.Current);
        // The updater: the app's only network use (see UpdateService and the privacy text); nothing happens when the build has no
        // update feed or signing key (Updates/update-config.json).
        var currentVersion = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _updateClient = new HttpUpdateClient(currentVersion.ToString(3));
        _updateService = new UpdateService(UpdateConfiguration.LoadEmbedded(), _updateClient, preferences, currentVersion);
        var updates = new UpdatesViewModel(_updateService, RunInstallerAndExit, SynchronizationContext.Current);

        _mainViewModel = new MainViewModel(
            captureViewModel, ocrViewModel, translationViewModel, overlayViewModel, workerViewModel, readiness,
            new AppearanceViewModel(_themeService, preferences), updates);
        _updateService.Start();

        _overlayWindow = new OverlayWindow(overlayViewModel, new OverlaySettingsStore());
        _overlayWindow.Attach();
        RegisterHotkeys(overlayViewModel);

        ShowMainWindow();
        overlayViewModel.SettingsRequested += (_, _) => ShowMainWindow();
        overlayViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OverlayViewModel.IsVisible))
            {
                EvaluateShutdown();
            }
        };

        await captureViewModel.InitializeAsync();
    }

    /// <summary>Runs the verified installer the user chose to install, then closes GlyphLens so the installer can replace it.</summary>
    private void RunInstallerAndExit(string installer)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // cancelled at the Windows prompt, or the file could not be started: stay open
            return;
        }

        Shutdown();
    }

    /// <summary>Shows the main (configuration) window, creating it again if it was closed.</summary>
    private void ShowMainWindow()
    {
        if (MainWindow is { IsLoaded: true } existing)
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            _ = existing.Activate();
            return;
        }

        var window = new MainWindow { DataContext = _mainViewModel };
        window.Closed += (_, _) => EvaluateShutdown();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// The application ends when the main window is closed and the overlay is not visible. Closing the main window while the
    /// overlay is showing keeps the translation session alive; hiding the overlay afterwards then ends the application.
    /// </summary>
    private void EvaluateShutdown()
    {
        bool mainOpen = MainWindow is { IsLoaded: true };
        bool overlayVisible = _mainViewModel?.Overlay.IsVisible == true;
        if (!mainOpen && !overlayVisible)
        {
            Shutdown();
        }
    }

    /// <summary>Opens (creating it if needed) the folder where the local OCR models belong, in Explorer.</summary>
    private static void OpenOcrFolder(OcrViewModel ocr)
    {
        string? models = ModelsDirectory.Resolve(Environment.GetEnvironmentVariable);
        if (models is null)
        {
            ocr.ShowEngineAdvice(new OcrAdvice(false, OcrAdviceSeverity.Problem, "No models folder could be determined (LOCALAPPDATA is not set)."));
            return;
        }

        try
        {
            string folder = ModelsDirectory.OcrFolder(models);
            _ = Directory.CreateDirectory(folder);
            _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ocr.ShowEngineAdvice(new OcrAdvice(false, OcrAdviceSeverity.Problem, "The OCR models folder could not be opened."));
        }
    }

    private void RegisterHotkeys(OverlayViewModel overlay)
    {
        try
        {
            _hotkeys = new GlobalHotkeyService(new Win32HotkeyNative());
            var results = _hotkeys.RegisterAll();
            overlay.ShortcutsText = string.Join(
                " ",
                results.Select(r => r.Registered ? $"{r.Binding.Display}: {Describe(r.Binding.Action)}." : r.Problem));
            _hotkeys.ActionRequested += (_, action) =>
            {
                switch (action)
                {
                    case OverlayHotkeyAction.ToggleTranslation:
                        overlay.ToggleTranslation();
                        break;
                    case OverlayHotkeyAction.ToggleOverlay:
                        overlay.ToggleVisible();
                        break;
                    case OverlayHotkeyAction.TogglePause:
                        overlay.TogglePause();
                        break;
                    case OverlayHotkeyAction.ToggleInteractive:
                        overlay.ToggleInteractive();
                        break;
                    case OverlayHotkeyAction.ClearResults:
                        overlay.ClearSession();
                        break;
                }
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            overlay.ShortcutsText = "Shortcuts are unavailable on this system.";
        }
    }

    private static string Describe(OverlayHotkeyAction action) => action switch
    {
        OverlayHotkeyAction.ToggleTranslation => "translation on/off",
        OverlayHotkeyAction.ToggleOverlay => "show/hide overlay",
        OverlayHotkeyAction.TogglePause => "pause/resume",
        OverlayHotkeyAction.ToggleInteractive => "interactive/pass-through",
        OverlayHotkeyAction.ClearResults => "clear results",
        _ => action.ToString(),
    };

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
}
