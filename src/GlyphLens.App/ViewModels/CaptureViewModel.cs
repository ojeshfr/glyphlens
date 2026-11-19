using System.Collections.ObjectModel;
using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>
/// State and commands of the capture diagnostic view: choose a display or window, start and stop capturing,
/// and show the capture status, frame information and errors.
/// </summary>
/// <remarks>
/// <para>
/// Everything shown here is derived from the <see cref="IScreenCaptureService"/>'s real state; nothing is
/// assumed. Service callbacks arrive on arbitrary threads and are marshalled to the synchronisation context
/// captured at construction (the UI thread in the application; inline in unit tests).
/// </para>
/// <para>Frames themselves are not handled here: the preview control reads <see cref="Frames"/> directly.</para>
/// </remarks>
public sealed class CaptureViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromMilliseconds(250);

    private readonly IScreenCaptureService _service;
    private readonly ICaptureTargetProvider _provider;
    private readonly SynchronizationContext? _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<CaptureTargetOption> _options = [];

    private IReadOnlyList<CaptureTarget> _allTargets = [];
    private CaptureTargetKind _selectedKind = CaptureTargetKind.Display;
    private CaptureTargetOption? _selectedOption;
    private CaptureStatus _status;
    private string? _errorMessage;
    private string? _errorDetail;
    private string _frameSizeText = "—";
    private string _frameSequenceText = "—";
    private string _targetInfoText = string.Empty;
    private bool _isRefreshing;
    private CancellationTokenSource? _metricsCts;
    private bool _disposed;

    /// <summary>Creates the view model on the UI thread; service callbacks are marshalled to that thread's context.</summary>
    public CaptureViewModel(IScreenCaptureService service, ICaptureTargetProvider provider)
        : this(service, provider, SynchronizationContext.Current)
    {
    }

    /// <param name="service">The capture service whose state this view model reflects.</param>
    /// <param name="provider">Enumerates the displays and windows that can be captured.</param>
    /// <param name="uiContext">
    /// Where service callbacks are delivered. Exactly this context is used; <see langword="null"/> means
    /// "run inline on the calling thread" (it does not fall back to the ambient context).
    /// </param>
    public CaptureViewModel(IScreenCaptureService service, ICaptureTargetProvider provider, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(provider);

        _service = service;
        _provider = provider;
        _ui = uiContext;
        _status = service.Status;

        Options = new ReadOnlyObservableCollection<CaptureTargetOption>(_options);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart, uiContext);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanStop, uiContext);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isRefreshing && !IsBusy, uiContext);

        _service.StatusChanged += OnServiceStatusChanged;
    }

    /// <summary>Latest-frame source the preview control renders from.</summary>
    public IFrameSource Frames => _service.Frames;

    /// <summary>Targets of the currently selected kind.</summary>
    public ReadOnlyObservableCollection<CaptureTargetOption> Options { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public CaptureTargetKind SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (!SetProperty(ref _selectedKind, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDisplayKind));
            OnPropertyChanged(nameof(IsWindowKind));
            RebuildOptions(keepSelectionId: null);
        }
    }

    public bool IsDisplayKind
    {
        get => SelectedKind == CaptureTargetKind.Display;
        set
        {
            if (value)
            {
                SelectedKind = CaptureTargetKind.Display;
            }
        }
    }

    public bool IsWindowKind
    {
        get => SelectedKind == CaptureTargetKind.Window;
        set
        {
            if (value)
            {
                SelectedKind = CaptureTargetKind.Window;
            }
        }
    }

    public CaptureTargetOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public CaptureStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsCapturing));
                OnPropertyChanged(nameof(CanChooseTarget));
                RaiseCommandStates();
            }
        }
    }

    public string StatusText => Status switch
    {
        CaptureStatus.Idle => "Not capturing",
        CaptureStatus.Starting => "Starting…",
        CaptureStatus.Capturing => "Capturing",
        CaptureStatus.Stopping => "Stopping…",
        CaptureStatus.Faulted => "Capture stopped by an error",
        _ => Status.ToString(),
    };

    public bool IsCapturing => Status == CaptureStatus.Capturing;

    /// <summary>The target selection can only change while no capture is starting, running or stopping.</summary>
    public bool CanChooseTarget => Status is CaptureStatus.Idle or CaptureStatus.Faulted;

    public bool CanStart => CanChooseTarget && SelectedOption is not null;

    public bool CanStop => Status == CaptureStatus.Capturing;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Technical detail (exception type and HRESULT) for the error, for tooltips and bug reports.</summary>
    public string? ErrorDetail
    {
        get => _errorDetail;
        private set => SetProperty(ref _errorDetail, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string FrameSizeText
    {
        get => _frameSizeText;
        private set => SetProperty(ref _frameSizeText, value);
    }

    public string FrameSequenceText
    {
        get => _frameSequenceText;
        private set => SetProperty(ref _frameSequenceText, value);
    }

    /// <summary>Description of the captured target while capturing (name and pixel size).</summary>
    public string TargetInfoText
    {
        get => _targetInfoText;
        private set => SetProperty(ref _targetInfoText, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private bool IsBusy => Status is CaptureStatus.Starting or CaptureStatus.Stopping;

    /// <summary>Loads the available targets. Call once after construction.</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Re-reads the frame information from the service. Called periodically while capturing.</summary>
    public void RefreshFrameInfo()
    {
        var metadata = _service.Frames.LatestMetadata;
        if (metadata is null || Status != CaptureStatus.Capturing)
        {
            FrameSizeText = "—";
            FrameSequenceText = "—";
            return;
        }

        FrameSizeText = string.Create(CultureInfo.InvariantCulture, $"{metadata.Width} × {metadata.Height} px");
        FrameSequenceText = metadata.Sequence.ToString("N0", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.StatusChanged -= OnServiceStatusChanged;
        StopMetrics();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task StartAsync()
    {
        var option = SelectedOption;
        if (option is null || !CanStart)
        {
            return;
        }

        ClearError();
        try
        {
            await _service.StartAsync(option.Target, _lifetime.Token).ConfigureAwait(true);
        }
        catch (CaptureException ex)
        {
            ShowError(ex.Error);
            if (ex.Error.Kind is CaptureErrorKind.TargetNotFound or CaptureErrorKind.TargetClosed)
            {
                await RefreshAsync().ConfigureAwait(true); // the list is stale: show what is really available
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by closing the application; not an error.
        }
        catch (InvalidOperationException ex)
        {
            ShowError(new CaptureError(CaptureErrorKind.Unexpected, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(new CaptureError(CaptureErrorKind.Unexpected, "Capture could not be started.") { Detail = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _service.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(new CaptureError(CaptureErrorKind.Unexpected, "Capture could not be stopped cleanly.") { Detail = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var selectedId = SelectedOption?.Target.Id;
            _allTargets = await Task.Run(_provider.GetTargets).ConfigureAwait(true);
            RebuildOptions(selectedId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(new CaptureError(CaptureErrorKind.Unexpected, "The list of displays and windows could not be read.") { Detail = $"{ex.GetType().Name}: {ex.Message}" });
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RebuildOptions(string? keepSelectionId)
    {
        _options.Clear();
        foreach (var target in _allTargets.Where(t => t.Kind == SelectedKind))
        {
            _options.Add(new CaptureTargetOption(target));
        }

        SelectedOption =
            _options.FirstOrDefault(o => o.Target.Id == keepSelectionId)
            ?? _options.FirstOrDefault(o => o.Target.IsPrimary)
            ?? _options.FirstOrDefault();
    }

    private void OnServiceStatusChanged(object? sender, CaptureStatusChangedEventArgs args) =>
        Post(() => ApplyStatus(args));

    private void ApplyStatus(CaptureStatusChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        Status = args.CurrentStatus;
        switch (args.CurrentStatus)
        {
            case CaptureStatus.Capturing:
                TargetInfoText = _service.CurrentTarget is { } target ? CaptureTargetOption.Describe(target) : string.Empty;
                StartMetrics();
                break;

            case CaptureStatus.Faulted:
                StopMetrics();
                TargetInfoText = string.Empty;
                RefreshFrameInfo();
                if (args.Error is not null)
                {
                    ShowError(args.Error);
                }

                break;

            case CaptureStatus.Idle:
                StopMetrics();
                TargetInfoText = string.Empty;
                RefreshFrameInfo();
                break;

            default:
                break;
        }
    }

    private void StartMetrics()
    {
        StopMetrics();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metricsCts = cts;
        _ = RunMetricsAsync(cts.Token);
    }

    private void StopMetrics()
    {
        var cts = _metricsCts;
        _metricsCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task RunMetricsAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(MetricsInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Post(RefreshFrameInfo);
            }
        }
        catch (OperationCanceledException)
        {
            // Capture ended or the view model was disposed.
        }
    }

    private void ShowError(CaptureError error)
    {
        ErrorMessage = error.Message;
        ErrorDetail = error.Detail;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorDetail = null;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
