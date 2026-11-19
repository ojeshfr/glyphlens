using System.Windows;
using GlyphLens.App.ViewModels;
using GlyphLens.Windows.Capture;
using GlyphLens.Windows.Display;

namespace GlyphLens.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// Services are composed here; windows and view models receive them through constructors. Capture is stopped and
/// its native resources are released when the application exits, so a capture can never outlive the app.
/// </remarks>
public partial class App : Application, IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private WindowsGraphicsCaptureService? _captureService;
    private MainViewModel? _mainViewModel;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mainViewModel?.Dispose();

        // Bounded, so a stuck driver call can never keep the application from exiting.
        _ = _captureService?.DisposeAsync().AsTask().Wait(ShutdownTimeout);
        GC.SuppressFinalize(this);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _captureService = new WindowsGraphicsCaptureService();
        var captureViewModel = new CaptureViewModel(_captureService, new CaptureTargetProvider());
        _mainViewModel = new MainViewModel(captureViewModel);

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel,
        };

        MainWindow = mainWindow;
        mainWindow.Show();

        await captureViewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
}
