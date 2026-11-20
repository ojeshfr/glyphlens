using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.ViewModels;

/// <summary>A recognised line shown in the results list.</summary>
public sealed record OcrLineItem(string Text, string BoundsText);

/// <summary>A selectable recognition language. <see cref="Tag"/> is <see langword="null"/> for "automatic".</summary>
public sealed record OcrLanguageOption(string? Tag, string Label);

/// <summary>
/// State and commands of the text-recognition panel: read the text in the latest captured frame once, or keep reading
/// it live, and show the result as a list and as boxes over the preview.
/// </summary>
/// <remarks>
/// <para>
/// Recognition always works on the <b>latest</b> captured frame and never queues: the live loop waits for a newer frame
/// only after it finished the previous one, and no faster than <c>liveInterval</c>, so a slow engine just lowers the
/// refresh rate. Recognised text lives only in memory (this view model); it is never logged or persisted, and it is
/// cleared when capture stops. Copying to the clipboard happens only on the user's command.
/// </para>
/// </remarks>
public sealed class OcrViewModel : ViewModelBase, IDisposable
{
    /// <summary>Default minimum time between live recognitions.</summary>
    public static readonly TimeSpan DefaultLiveInterval = TimeSpan.FromMilliseconds(600);

    private readonly IOcrEngine _engine;
    private readonly IOcrLanguageSource _languages;
    private readonly IScreenCaptureService _capture;
    private readonly OverlayState _overlay;
    private readonly Action<string>? _copyToClipboard;
    private readonly SynchronizationContext? _ui;
    private readonly TimeSpan _liveInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<OcrLineItem> _lines = [];
    private readonly ObservableCollection<OcrLanguageOption> _languageOptions = [];

    private OcrLanguageOption _selectedLanguage;
    private CancellationTokenSource? _liveCts;
    private Task _liveTask = Task.CompletedTask;
    private string _statusText = "Start a capture, then read its text.";
    private string? _errorMessage;
    private int _readingCount;
    private bool _isLive;
    private bool _disposed;

    public OcrViewModel(
        IOcrEngine engine,
        IOcrLanguageSource languages,
        IScreenCaptureService capture,
        OverlayState overlay,
        Action<string>? copyToClipboard = null)
        : this(engine, languages, capture, overlay, copyToClipboard, SynchronizationContext.Current, DefaultLiveInterval)
    {
    }

    /// <param name="engine">The OCR engine.</param>
    /// <param name="languages">Source of the selectable languages (usually the same object as the engine).</param>
    /// <param name="capture">The capture service whose latest frame is read and whose status ends recognition.</param>
    /// <param name="overlay">Receives the boxes drawn over the preview.</param>
    /// <param name="copyToClipboard">Called with the recognised text when the user chooses Copy.</param>
    /// <param name="uiContext">Where capture callbacks are delivered; <see langword="null"/> means inline (no ambient fallback).</param>
    /// <param name="liveInterval">Minimum time between live recognitions.</param>
    public OcrViewModel(
        IOcrEngine engine,
        IOcrLanguageSource languages,
        IScreenCaptureService capture,
        OverlayState overlay,
        Action<string>? copyToClipboard,
        SynchronizationContext? uiContext,
        TimeSpan liveInterval)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(overlay);

        _engine = engine;
        _languages = languages;
        _capture = capture;
        _overlay = overlay;
        _copyToClipboard = copyToClipboard;
        _ui = uiContext;
        _liveInterval = liveInterval;

        Lines = new ReadOnlyObservableCollection<OcrLineItem>(_lines);
        Languages = new ReadOnlyObservableCollection<OcrLanguageOption>(_languageOptions);

        _languageOptions.Add(new OcrLanguageOption(null, "Automatic (your Windows languages)"));
        foreach (var language in languages.AvailableLanguages)
        {
            _languageOptions.Add(new OcrLanguageOption(language.Tag, language.DisplayName));
        }

        _selectedLanguage = _languageOptions.FirstOrDefault(o => string.Equals(o.Tag, languages.SelectedLanguage, StringComparison.OrdinalIgnoreCase))
            ?? _languageOptions[0];

        if (languages.AvailableLanguages.Count == 0)
        {
            _errorMessage = "No Windows OCR language is installed. Add an OCR-capable language in Windows Settings (Time & language).";
        }

        ReadTextCommand = new AsyncRelayCommand(ReadOnceAsync, () => CanReadText, uiContext);
        CopyTextCommand = new AsyncRelayCommand(CopyAsync, () => CanCopy, uiContext);

        _capture.StatusChanged += OnCaptureStatusChanged;
    }

    public ReadOnlyObservableCollection<OcrLineItem> Lines { get; }

    public ReadOnlyObservableCollection<OcrLanguageOption> Languages { get; }

    public AsyncRelayCommand ReadTextCommand { get; }

    public AsyncRelayCommand CopyTextCommand { get; }

    public OcrLanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            _languages.SelectedLanguage = value.Tag;
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsCapturing => _capture.Status == CaptureStatus.Capturing;

    public bool IsReading => Volatile.Read(ref _readingCount) > 0;

    /// <summary>Whether text is being re-read continuously. Setting it starts or stops the live loop.</summary>
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (value == _isLive)
            {
                return;
            }

            if (value && !IsCapturing)
            {
                OnPropertyChanged(); // refuse: snap the toggle back
                StatusText = "Start a capture before turning on live reading.";
                return;
            }

            if (value)
            {
                StartLive();
            }
            else
            {
                StopLive();
            }
        }
    }

    public bool CanReadText => IsCapturing && !IsReading && !_isLive;

    public bool CanCopy => _lines.Count > 0;

    /// <summary>The recognised text, one line per row.</summary>
    public string RecognisedText => string.Join(Environment.NewLine, _lines.Select(l => l.Text));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.StatusChanged -= OnCaptureStatusChanged;
        StopLive();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Waits for the live loop to finish (for tests and orderly shutdown).</summary>
    public Task WaitForLiveStoppedAsync() => _liveTask;

    private async Task ReadOnceAsync()
    {
        if (!CanReadText)
        {
            return;
        }

        await RecogniseLatestAsync(_lifetime.Token).ConfigureAwait(true);
    }

    private async Task<bool> RecogniseLatestAsync(CancellationToken cancellationToken)
    {
        if (_capture.Frames.TryAcquireLatest() is not { } latest)
        {
            ErrorMessage = "No frame has been captured yet.";
            return false;
        }

        using (latest.Frame)
        {
            return await RecogniseAsync(latest.Frame, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<bool> RecogniseAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readingCount);
        Refresh();
        try
        {
            var watch = Stopwatch.StartNew();
            var regions = await _engine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(true);
            watch.Stop();

            Publish(regions, frame.Metadata, watch.Elapsed);
            ErrorMessage = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (OcrException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never include the exception message: it could in principle carry captured text.
            ErrorMessage = $"Text recognition failed unexpectedly ({ex.GetType().Name}).";
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _readingCount);
            Refresh();
        }
    }

    private void Publish(IReadOnlyList<ScreenTextRegion> regions, FrameMetadata frame, TimeSpan elapsed)
    {
        _lines.Clear();
        var boxes = new List<OcrBox>(regions.Count);
        foreach (var region in regions)
        {
            _lines.Add(new OcrLineItem(region.Text, string.Create(CultureInfo.InvariantCulture, $"{region.X},{region.Y}  {region.Width}×{region.Height}")));
            boxes.Add(new OcrBox(region.X, region.Y, region.Width, region.Height, region.Text));
        }

        _overlay.Set(boxes, frame.Width, frame.Height);

        var language = regions.Count > 0 ? regions[0].SourceLanguage : null;
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{regions.Count} line{(regions.Count == 1 ? string.Empty : "s")} · {elapsed.TotalMilliseconds:0} ms{(language is null ? string.Empty : " · " + language)}");
        OnPropertyChanged(nameof(RecognisedText));
    }

    private void StartLive()
    {
        StopLive();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _liveCts = cts;
        _isLive = true;
        OnPropertyChanged(nameof(IsLive));
        StatusText = "Live reading…";
        Refresh();
        _liveTask = RunLiveAsync(cts);
    }

    private void StopLive()
    {
        var cts = _liveCts;
        _liveCts = null;
        cts?.Cancel();
        if (_isLive)
        {
            _isLive = false;
            OnPropertyChanged(nameof(IsLive));
            Refresh();
        }
    }

    private async Task RunLiveAsync(CancellationTokenSource source)
    {
        var cancellationToken = source.Token;
        // Start at 0 so the frame that is already on screen is read immediately: display capture only produces a new frame
        // when the picture changes, so waiting for a "newer" one first would read nothing on a static screen.
        long seen = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (await _capture.Frames.WaitForNewerAsync(seen, cancellationToken).ConfigureAwait(true) is not { } latest)
                {
                    break; // the frame source was disposed
                }

                bool ok;
                using (latest.Frame)
                {
                    seen = latest.Version;
                    ok = await RecogniseAsync(latest.Frame, cancellationToken).ConfigureAwait(true);
                }

                if (!ok && HasError)
                {
                    break; // an engine problem would repeat every cycle; stop and show it
                }

                var remaining = _liveInterval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, by capture ending, or by disposal.
        }
        finally
        {
            // If the loop ended by itself (engine error, source disposed) rather than through StopLive, reset the toggle.
            if (ReferenceEquals(_liveCts, source) && _isLive)
            {
                _liveCts = null;
                _isLive = false;
                OnPropertyChanged(nameof(IsLive));
                Refresh();
            }
        }
    }

    private Task CopyAsync()
    {
        if (_copyToClipboard is null || _lines.Count == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            _copyToClipboard(RecognisedText);
            StatusText = "Copied the recognised text.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ErrorMessage = "The clipboard is busy; try again.";
        }

        return Task.CompletedTask;
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusChangedEventArgs args)
    {
        if (_ui is null)
        {
            ApplyCaptureStatus();
        }
        else
        {
            _ui.Post(_ => ApplyCaptureStatus(), null);
        }
    }

    private void ApplyCaptureStatus()
    {
        if (_disposed)
        {
            return;
        }

        if (!IsCapturing)
        {
            // Results describe a screen that is no longer being captured: clear them rather than leave stale text.
            StopLive();
            _lines.Clear();
            _overlay.Clear();
            ErrorMessage = _languages.AvailableLanguages.Count == 0 ? ErrorMessage : null;
            StatusText = "Start a capture, then read its text.";
            OnPropertyChanged(nameof(RecognisedText));
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(IsReading));
        OnPropertyChanged(nameof(CanReadText));
        OnPropertyChanged(nameof(CanCopy));
        ReadTextCommand.RaiseCanExecuteChanged();
        CopyTextCommand.RaiseCanExecuteChanged();
    }
}
