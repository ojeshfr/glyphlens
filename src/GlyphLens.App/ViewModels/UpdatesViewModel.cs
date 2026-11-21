using System.Globalization;
using GlyphLens.App.Mvvm;
using GlyphLens.App.Services;

namespace GlyphLens.App.ViewModels;

/// <summary>The "Updates" section of Settings and the update banner on the Overview.</summary>
public sealed class UpdatesViewModel : ViewModelBase, IDisposable
{
    private readonly UpdateService _service;
    private readonly Action<string> _launchInstaller;
    private readonly SynchronizationContext? _ui;

    public UpdatesViewModel(UpdateService service, Action<string> launchInstaller, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(launchInstaller);
        _service = service;
        _launchInstaller = launchInstaller;
        _ui = uiContext;
        CheckNowCommand = new AsyncRelayCommand(() => _service.CheckAsync(), () => CanCheck, uiContext);
        DownloadCommand = new AsyncRelayCommand(() => _service.DownloadAsync(), () => CanDownload, uiContext);
        InstallCommand = new AsyncRelayCommand(Install, () => CanInstall, uiContext);
        _service.Changed += OnServiceChanged;
    }

    public AsyncRelayCommand CheckNowCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public bool IsConfigured => _service.IsConfigured;

    public bool AutomaticChecks
    {
        get => _service.AutomaticChecks;
        set => _service.AutomaticChecks = value;
    }

    public string CurrentVersionText => $"GlyphLens {_service.CurrentVersion.ToString(3)}";

    public bool CanCheck => IsConfigured && _service.State is not (UpdateState.Checking or UpdateState.Downloading);

    public bool CanDownload => _service.Available is not null && _service.State is UpdateState.Available or UpdateState.Failed;

    public bool CanInstall => _service.State == UpdateState.ReadyToInstall;

    /// <summary>A newer version is known (shown as a banner on the Overview).</summary>
    public bool IsUpdateAvailable => _service.Available is not null && _service.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall;

    public string BannerText => _service.Available is { } release ? $"GlyphLens {release.Version.ToString(3)} is available." : string.Empty;

    public string Notes => _service.Available?.Notes ?? string.Empty;

    public bool HasNotes => Notes.Length > 0;

    public string StatusText
    {
        get
        {
            var release = _service.Available;
            string version = release?.Version.ToString(3) ?? string.Empty;
            return _service.State switch
            {
                UpdateState.NotConfigured => "Updates are not set up in this build (it has no release signing key), so GlyphLens does not contact any update server.",
                UpdateState.Checking => "Checking for updates…",
                UpdateState.UpToDate => $"{CurrentVersionText} is the latest version.",
                UpdateState.Available => $"GlyphLens {version} is available ({Megabytes(release!.Size)}).",
                UpdateState.Downloading => string.Create(CultureInfo.InvariantCulture, $"Downloading GlyphLens {version}… {_service.Progress * 100:0}%"),
                UpdateState.ReadyToInstall => $"GlyphLens {version} is downloaded and verified. Installing closes GlyphLens while the installer runs.",
                UpdateState.Failed => _service.Problem ?? "The last update step failed.",
                _ => AutomaticChecks ? "GlyphLens looks for updates shortly after it starts and then about once a day." : "Automatic checks are off.",
            };
        }
    }

    public string LastCheckedText => _service.LastChecked is { } at
        ? "Last checked " + at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + "."
        : "Not checked yet.";

    public void Dispose() => _service.Changed -= OnServiceChanged;

    private Task Install()
    {
        _service.Install(_launchInstaller);
        return Task.CompletedTask;
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            Refresh();
        }
        else
        {
            _ui.Post(_ => Refresh(), null);
        }
    }

    private void Refresh()
    {
        foreach (string name in new[]
                 {
                     nameof(StatusText), nameof(LastCheckedText), nameof(CanCheck), nameof(CanDownload), nameof(CanInstall), nameof(IsUpdateAvailable),
                     nameof(BannerText), nameof(Notes), nameof(HasNotes), nameof(AutomaticChecks),
                 })
        {
            OnPropertyChanged(name);
        }

        CheckNowCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
    }
}
