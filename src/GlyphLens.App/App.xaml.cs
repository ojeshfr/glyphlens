using System.Windows;
using GlyphLens.App.ViewModels;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Windows.Capture;
using GlyphLens.Windows.Display;
using GlyphLens.Windows.Worker;

namespace GlyphLens.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// Services are composed here; windows and view models receive them through constructors. Capture is stopped and the
/// worker is shut down when the application exits, so neither can outlive the app. The worker is never started
/// automatically: the user starts it from the diagnostics panel.
/// </remarks>
public partial class App : Application, IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private WindowsGraphicsCaptureService? _captureService;
    private PythonWorkerProcessLauncher? _workerLauncher;
    private WorkerProcessManager? _workerManager;
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

        _captureService = new WindowsGraphicsCaptureService();
        var captureViewModel = new CaptureViewModel(_captureService, new CaptureTargetProvider());

        _workerLauncher = new PythonWorkerProcessLauncher();
        _workerManager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), _workerLauncher, log: new TraceWorkerLog());
        var workerViewModel = new WorkerViewModel(_workerManager);

        _mainViewModel = new MainViewModel(captureViewModel, workerViewModel);

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
