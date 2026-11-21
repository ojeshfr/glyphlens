using System.Collections.ObjectModel;
using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.ViewModels;

/// <summary>Raised by <see cref="TranslationViewModel"/> after each translation pass, with the pass's results.</summary>
public sealed class TranslationsPublishedEventArgs(IReadOnlyList<RegionTranslation> results, DateTimeOffset publishedAt) : EventArgs
{
    /// <summary>One entry per translation unit (a line or a group of lines), in reading order, with identity and coordinates.</summary>
    public IReadOnlyList<RegionTranslation> Results { get; } = results;

    public DateTimeOffset PublishedAt { get; } = publishedAt;
}

/// <summary>One translation shown in the overlay. Updated in place when the same screen text changes, never recreated.</summary>
public sealed class OverlayCard : ViewModelBase
{
    private string _original = string.Empty;
    private string? _translated;
    private string _note = string.Empty;
    private string _language = string.Empty;
    private string _confidence = string.Empty;
    private DateTimeOffset _updatedAt;
    private bool _isProblem;

    public OverlayCard(RegionTranslation result, DateTimeOffset at)
    {
        Apply(result, at);
    }

    public string Original
    {
        get => _original;
        private set => SetProperty(ref _original, value);
    }

    public string? Translated
    {
        get => _translated;
        private set
        {
            if (SetProperty(ref _translated, value))
            {
                OnPropertyChanged(nameof(HasTranslation));
            }
        }
    }

    public bool HasTranslation => _translated is not null;

    /// <summary>Why there is no translation (language uncertain, model missing, ...), or empty.</summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => _note.Length > 0;

    public bool IsProblem
    {
        get => _isProblem;
        private set => SetProperty(ref _isProblem, value);
    }

    /// <summary>The source language, for example "Chinese" or "Chinese (chosen)".</summary>
    public string Language
    {
        get => _language;
        private set => SetProperty(ref _language, value);
    }

    /// <summary>The recogniser's own confidence for this text, shown only when it reported one (for example "OCR 98%").</summary>
    public string Confidence
    {
        get => _confidence;
        private set
        {
            if (SetProperty(ref _confidence, value))
            {
                OnPropertyChanged(nameof(HasConfidence));
            }
        }
    }

    public bool HasConfidence => _confidence.Length > 0;

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        private set
        {
            if (SetProperty(ref _updatedAt, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    public string TimeText => UpdatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>One line under the translation: the language, the time and, when the recogniser reported one, its confidence.</summary>
    public string Details => HasConfidence ? $"{Language} · {TimeText} · {Confidence}" : $"{Language} · {TimeText}";

    /// <summary>Where the text is in the captured frame; used to recognise "the same screen region" on the next pass.</summary>
    internal (int X, int Y, int Width, int Height) Bounds { get; private set; }

    internal string NormalizedOriginal { get; private set; } = string.Empty;

    internal void Apply(RegionTranslation result, DateTimeOffset at)
    {
        Original = result.Region.Text;
        NormalizedOriginal = TextNormalizer.Normalize(result.Region.Text);
        Translated = result.IsTranslated ? result.TranslatedText : null;
        Note = result.IsTranslated ? string.Empty : result.Message ?? string.Empty;
        IsProblem = result.Status is not (RegionTranslationStatus.Translated or RegionTranslationStatus.AlreadyTarget or RegionTranslationStatus.NoText);
        Language = DescribeLanguage(result);
        Confidence = result.Region.Confidence is { } c ? string.Create(CultureInfo.InvariantCulture, $"OCR {c * 100:0}%") : string.Empty;
        Bounds = (result.Region.X, result.Region.Y, result.Region.Width, result.Region.Height);
        UpdatedAt = at;
        OnPropertyChanged(nameof(Details));
    }

    private static string DescribeLanguage(RegionTranslation r)
    {
        if (r.SourceLanguage is null)
        {
            return "Language unknown";
        }

        string name = LanguageNames.Display(r.SourceLanguage);
        return r.SourceChosenByUser ? name + " (chosen)" : name;
    }
}

/// <summary>
/// State and commands of the floating translation overlay: the translation cards, the window modes (pinned, interactive or
/// pass-through, collapsed, opacity, visible) and the shortcut actions.
/// </summary>
/// <remarks>
/// <para>
/// The overlay does not recognise or translate anything itself. It listens to <see cref="TranslationViewModel"/>, which is
/// fed by the one OCR and translation pipeline, and turns each pass into cards. A card is updated in place when the same
/// text (or the same screen region) comes back, so a Live update changes only the cards that changed; the list is bounded
/// (<see cref="MaxCards"/>) and the newest card is last, so the panel does not jump when results arrive.
/// </para>
/// <para>Cards hold screen text: they live in memory only, are cleared by <see cref="ClearSessionCommand"/>, and are never persisted.</para>
/// </remarks>
public sealed class OverlayViewModel : ViewModelBase, IDisposable
{
    public const int MaxCards = 200;
    public const double MinOpacity = 0.35;

    /// <summary>Smallest, default and largest size of the card text, in DIPs (the translation is one DIP larger).</summary>
    public const double MinFontSize = 11;

    public const double DefaultFontSize = 13;

    public const double MaxFontSize = 20;

    /// <summary>How long a requested "clear" waits for its confirmation.</summary>
    public static readonly TimeSpan ClearConfirmationTimeout = TimeSpan.FromSeconds(5);

    private readonly TranslationViewModel _translation;
    private readonly OcrViewModel? _ocr;
    private readonly TimeProvider _time;
    private readonly Action<string>? _copy;
    private CancellationTokenSource? _clearConfirmation;
    private double _fontSize = DefaultFontSize;
    private bool _isConfirmingClear;
    private bool _hasUnseenResults;
    private OverlayStatus _status = new(OverlayStateKind.Off, "Off", StatusLevel.Neutral);
    private readonly ObservableCollection<OverlayCard> _cards = [];
    private bool _isPinned = true;
    private bool _isInteractive = true;
    private bool _isCollapsed;
    private bool _isVisible;
    private double _opacity = 1.0;
    private string _statusMessage = string.Empty;
    private string _captureProtectionText = "Capture protection: not applied yet";
    private string _shortcutsText = "Shortcuts: not registered";
    private bool _disposed;

    /// <param name="translation">The translation view model the cards come from.</param>
    /// <param name="copyToClipboard">Called with the text of a copy button.</param>
    /// <param name="ocr">The recognition view model (for "capture stopped" and "live"); <see langword="null"/> when not known.</param>
    /// <param name="timeProvider">The clock used for the clear confirmation timeout.</param>
    public OverlayViewModel(TranslationViewModel translation, Action<string>? copyToClipboard = null, OcrViewModel? ocr = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(translation);

        _translation = translation;
        _ocr = ocr;
        _time = timeProvider ?? TimeProvider.System;
        _copy = copyToClipboard;
        Cards = new ReadOnlyObservableCollection<OverlayCard>(_cards);
        _cards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoCards));

        CopyOriginalCommand = new RelayCommand<OverlayCard>(card => Copy(card?.Original, "original"), card => card is not null);
        CopyTranslationCommand = new RelayCommand<OverlayCard>(card => Copy(card?.Translated, "translation"), card => card?.HasTranslation == true);
        ClearSessionCommand = new AsyncRelayCommand(() =>
        {
            RequestClear();
            return Task.CompletedTask;
        });
        CancelClearCommand = new DelegateCommand(CancelClear);
        IncreaseFontCommand = new DelegateCommand(() => FontSize += 1, () => FontSize < MaxFontSize);
        DecreaseFontCommand = new DelegateCommand(() => FontSize -= 1, () => FontSize > MinFontSize);

        ShowCommand = new DelegateCommand(Show);
        HideCommand = new DelegateCommand(Hide);
        ToggleVisibilityCommand = new DelegateCommand(ToggleVisible);
        ToggleInteractiveCommand = new DelegateCommand(ToggleInteractive);
        TogglePinnedCommand = new DelegateCommand(TogglePinned);
        ToggleCollapsedCommand = new DelegateCommand(ToggleCollapsed);
        SettingsCommand = new DelegateCommand(RequestSettings);
        TogglePauseCommand = new DelegateCommand(TogglePause);
        ToggleTranslationCommand = new DelegateCommand(ToggleTranslation);

        _translation.TranslationsPublished += OnPublished;
        _translation.PropertyChanged += OnTranslationPropertyChanged;
        if (_ocr is not null)
        {
            _ocr.PropertyChanged += OnOcrPropertyChanged;
        }

        RecomputeStatus();
    }

    /// <summary>The translation cards, oldest first (the newest is last).</summary>
    public ReadOnlyObservableCollection<OverlayCard> Cards { get; }

    public RelayCommand<OverlayCard> CopyOriginalCommand { get; }

    public RelayCommand<OverlayCard> CopyTranslationCommand { get; }

    public AsyncRelayCommand ClearSessionCommand { get; }

    public DelegateCommand ShowCommand { get; }

    public DelegateCommand HideCommand { get; }

    public DelegateCommand ToggleVisibilityCommand { get; }

    public DelegateCommand ToggleInteractiveCommand { get; }

    public DelegateCommand TogglePinnedCommand { get; }

    public DelegateCommand ToggleCollapsedCommand { get; }

    public DelegateCommand SettingsCommand { get; }

    public DelegateCommand TogglePauseCommand { get; }

    public DelegateCommand ToggleTranslationCommand { get; }

    public DelegateCommand CancelClearCommand { get; }

    public DelegateCommand IncreaseFontCommand { get; }

    public DelegateCommand DecreaseFontCommand { get; }

    /// <summary>The state shown in the header: what the overlay can truthfully say about itself right now.</summary>
    public OverlayStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateLevel));
            }
        }
    }

    public string StateText => _status.Text;

    public StatusLevel StateLevel => _status.Level;

    /// <summary>The size of the original text on a card, in DIPs (the translation is one DIP larger); persisted with the layout.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            double clamped = Math.Clamp(double.IsNaN(value) ? DefaultFontSize : Math.Round(value), MinFontSize, MaxFontSize);
            if (SetProperty(ref _fontSize, clamped))
            {
                OnPropertyChanged(nameof(TranslationFontSize));
                IncreaseFontCommand.RaiseCanExecuteChanged();
                DecreaseFontCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double TranslationFontSize => _fontSize + 1;

    /// <summary>Whether a "clear" was asked for and waits for its confirmation (it expires after <see cref="ClearConfirmationTimeout"/>).</summary>
    public bool IsConfirmingClear
    {
        get => _isConfirmingClear;
        private set => SetProperty(ref _isConfirmingClear, value);
    }

    /// <summary>Whether results arrived below the part of the list the user is reading (the window shows a "new results" pill).</summary>
    public bool HasUnseenResults
    {
        get => _hasUnseenResults;
        set => SetProperty(ref _hasUnseenResults, value);
    }

    /// <summary>Whether the overlay is excluded from screen capture (so it never translates itself), and why not when it is not.</summary>
    public string CaptureProtectionText
    {
        get => _captureProtectionText;
        set => SetProperty(ref _captureProtectionText, value);
    }

    /// <summary>The shortcuts that are active, and any that another program already owns.</summary>
    public string ShortcutsText
    {
        get => _shortcutsText;
        set => SetProperty(ref _shortcutsText, value);
    }

    /// <summary>Raised when the user asks for the main window (the settings shortcut).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Whether the overlay window is shown (the window follows this value).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Whether the overlay stays above ordinary windows.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>
    /// <see langword="true"/>: the overlay receives the mouse (select, copy, move, resize). <see langword="false"/>
    /// (pass-through): the mouse goes to the window underneath.
    /// </summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (SetProperty(ref _isInteractive, value))
            {
                OnPropertyChanged(nameof(IsPassThrough));
                OnPropertyChanged(nameof(ModeText));
            }
        }
    }

    public bool IsPassThrough => !_isInteractive;

    public string ModeText => _isInteractive ? "Interactive" : "Pass-through";

    /// <summary>Whether only the header is shown.</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (SetProperty(ref _isCollapsed, value))
            {
                OnPropertyChanged(nameof(IsExpanded));
            }
        }
    }

    public bool IsExpanded => !_isCollapsed;

    public bool HasNoCards => _cards.Count == 0;

    /// <summary>Window opacity from <see cref="MinOpacity"/> to 1 (a fully transparent overlay could not be found again).</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, Math.Clamp(double.IsNaN(value) ? 1.0 : value, MinOpacity, 1.0));
    }

    /// <summary>The source language as shown in the header: the Chinese scripts by their own names, "Auto" when detected.</summary>
    public string SourceText => _translation.SelectedSource.Code switch
    {
        null => "Auto",
        "zh" => "中文",
        "zt" => "繁體中文",
        _ => _translation.SelectedSource.Label,
    };

    public string TargetText => _translation.SelectedTarget.Label;

    /// <summary>The translation panel's status line (for example "3 of 4 translated · 1 cached").</summary>
    public string StatusText => _translation.StatusText;

    public bool IsTranslationActive => _translation.IsEnabled && !_translation.IsPaused;

    public bool IsPaused => _translation.IsPaused;

    public string ActivityText => !_translation.IsEnabled ? "Off" : _translation.IsPaused ? "Paused" : _translation.IsTranslating ? "Translating…" : "Active";

    public ReadOnlyObservableCollection<TranslationNotice> Notices => _translation.Notices;

    public bool HasNotices => _translation.HasNotices;

    /// <summary>The last action's outcome ("Copied the translation."), or empty.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ---- shortcut actions (also used by the header buttons) -----------------------------------------------------------

    public void ToggleTranslation() => _translation.IsEnabled = !_translation.IsEnabled;

    public void TogglePause() => _translation.IsPaused = !_translation.IsPaused;

    public void ToggleInteractive() => IsInteractive = !IsInteractive;

    public void TogglePinned() => IsPinned = !IsPinned;

    public void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    public void ToggleVisible() => IsVisible = !IsVisible;

    public void Hide() => IsVisible = false;

    public void Show()
    {
        IsVisible = true;
        IsCollapsed = false;
    }

    public void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Asks to clear the cards: the first call asks for confirmation (shown in the window and cancelled after
    /// <see cref="ClearConfirmationTimeout"/>), a second call while it is waiting clears.
    /// </summary>
    public void RequestClear()
    {
        if (_cards.Count == 0)
        {
            return;
        }

        if (IsConfirmingClear)
        {
            ClearSession();
            return;
        }

        IsConfirmingClear = true;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _clearConfirmation, cts)?.Cancel();
        _ = ExpireClearAsync(cts);
    }

    public void CancelClear()
    {
        Interlocked.Exchange(ref _clearConfirmation, null)?.Cancel();
        IsConfirmingClear = false;
    }

    /// <summary>Clears the cards now, without asking (the confirmed action).</summary>
    public void ClearSession()
    {
        Interlocked.Exchange(ref _clearConfirmation, null)?.Cancel();
        IsConfirmingClear = false;
        _cards.Clear();
        HasUnseenResults = false;
        StatusMessage = string.Empty;
    }

    private async Task ExpireClearAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(ClearConfirmationTimeout, _time, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(Interlocked.CompareExchange(ref _clearConfirmation, null, source), source))
        {
            IsConfirmingClear = false;
        }
    }

    private void RecomputeStatus()
    {
        Status = OverlayStatusEvaluator.Evaluate(new OverlayStatusInput(
            _translation.IsEnabled,
            _translation.IsPaused,
            _translation.WorkerStatus,
            _ocr?.IsCapturing,
            _ocr?.IsLive == true,
            _translation.LastPass));
    }

    private void OnOcrPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OcrViewModel.IsCapturing) or nameof(OcrViewModel.IsLive))
        {
            RecomputeStatus();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _translation.TranslationsPublished -= OnPublished;
        _translation.PropertyChanged -= OnTranslationPropertyChanged;
        if (_ocr is not null)
        {
            _ocr.PropertyChanged -= OnOcrPropertyChanged;
        }

        Interlocked.Exchange(ref _clearConfirmation, null)?.Cancel();
    }

    // ---- results -----------------------------------------------------------------------------------------------------

    private void OnPublished(object? sender, TranslationsPublishedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var result in args.Results)
        {
            if (FindCard(result) is { } existing)
            {
                existing.Apply(result, args.PublishedAt);
            }
            else
            {
                _cards.Add(new OverlayCard(result, args.PublishedAt));
            }
        }

        while (_cards.Count > MaxCards)
        {
            _cards.RemoveAt(0);
        }
    }

    /// <summary>The card for the same text, or for the same screen region with changed text; <see langword="null"/> when this is new.</summary>
    private OverlayCard? FindCard(RegionTranslation result)
    {
        string normalized = TextNormalizer.Normalize(result.Region.Text);
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_cards[i].NormalizedOriginal, normalized, StringComparison.Ordinal))
            {
                return _cards[i];
            }
        }

        var bounds = (result.Region.X, result.Region.Y, result.Region.Width, result.Region.Height);
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            if (Overlap(_cards[i].Bounds, bounds) >= 0.5)
            {
                return _cards[i];
            }
        }

        return null;
    }

    /// <summary>Intersection over union of two rectangles.</summary>
    public static double Overlap((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b)
    {
        long left = Math.Max(a.X, b.X);
        long top = Math.Max(a.Y, b.Y);
        long right = Math.Min(a.X + a.Width, b.X + b.Width);
        long bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        double intersection = (right - left) * (bottom - top);
        double union = ((long)a.Width * a.Height) + ((long)b.Width * b.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private void OnTranslationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranslationViewModel.SelectedSource):
                OnPropertyChanged(nameof(SourceText));
                break;
            case nameof(TranslationViewModel.SelectedTarget):
                OnPropertyChanged(nameof(TargetText));
                break;
            case nameof(TranslationViewModel.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(TranslationViewModel.HasNotices):
                OnPropertyChanged(nameof(HasNotices));
                break;
            case nameof(TranslationViewModel.IsEnabled):
            case nameof(TranslationViewModel.IsPaused):
            case nameof(TranslationViewModel.IsTranslating):
                OnPropertyChanged(nameof(IsTranslationActive));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(ActivityText));
                RecomputeStatus();
                break;
            case nameof(TranslationViewModel.WorkerStatus):
            case nameof(TranslationViewModel.LastPass):
                RecomputeStatus();
                break;
        }
    }

    private void Copy(string? text, string what)
    {
        if (_copy is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            _copy(text);
            StatusMessage = $"Copied the {what}.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusMessage = "The clipboard is busy; try again.";
        }
    }
}
