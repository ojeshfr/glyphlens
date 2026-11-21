using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Updates;

namespace GlyphLens.App.Services;

/// <summary>Where the update feed is and which key signs it; built into the application (Updates/update-config.json).</summary>
public sealed record UpdateConfiguration(Uri? FeedUrl, string PublicKeyPem)
{
    public const string ResourceName = "GlyphLens.UpdateConfig.json";

    /// <summary>Both a feed and a signing key are present; without them GlyphLens never contacts an update server.</summary>
    public bool IsConfigured => FeedUrl is not null && !string.IsNullOrWhiteSpace(PublicKeyPem);

    public static UpdateConfiguration NotConfigured { get; } = new(null, string.Empty);

    /// <summary>Reads the configuration built into this application; a missing or broken file means "not configured".</summary>
    public static UpdateConfiguration LoadEmbedded(Assembly? assembly = null)
    {
        try
        {
            using var stream = (assembly ?? typeof(UpdateConfiguration).Assembly).GetManifestResourceStream(ResourceName);
            return stream is null ? NotConfigured : Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return NotConfigured;
        }
    }

    public static UpdateConfiguration Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        string feed = document.RootElement.TryGetProperty("feedUrl", out var f) ? f.GetString() ?? string.Empty : string.Empty;
        string key = document.RootElement.TryGetProperty("publicKeyPem", out var k) ? k.GetString() ?? string.Empty : string.Empty;
        Uri? url = Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
        return new UpdateConfiguration(url, key);
    }
}

/// <summary>What the updater is doing.</summary>
public enum UpdateState
{
    /// <summary>No feed or no signing key in this build: nothing is ever checked.</summary>
    NotConfigured,

    /// <summary>Nothing checked yet in this session.</summary>
    Idle,

    Checking,

    /// <summary>The running version is the newest.</summary>
    UpToDate,

    /// <summary>A newer, correctly signed release exists.</summary>
    Available,

    Downloading,

    /// <summary>The installer is downloaded and its SHA-256 matches the signed manifest.</summary>
    ReadyToInstall,

    /// <summary>The last step failed; <see cref="UpdateService.Problem"/> says why.</summary>
    Failed,
}

/// <summary>
/// The integrated updater. With automatic checks on (the default) it asks the update feed shortly after start-up and then about once a
/// day (<see cref="UpdateSchedule"/>), verifies the feed's signature with the key built into GlyphLens, and reports a newer version. It
/// downloads an installer only when the user asks, keeps it only if size and SHA-256 match the signed manifest, checks the file again
/// right before running it, and runs it only when the user chooses to install. Nothing about the user or the screen is sent.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private readonly UpdateConfiguration _configuration;
    private readonly IUpdateClient _client;
    private readonly AppPreferencesStore _preferences;
    private readonly TimeProvider _time;
    private readonly string _downloadFolder;
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _lastFailure;
    private int _busy;
    private bool _automatic;

    public UpdateService(
        UpdateConfiguration configuration,
        IUpdateClient client,
        AppPreferencesStore preferences,
        Version currentVersion,
        string? downloadFolder = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(currentVersion);
        _configuration = configuration;
        _client = client;
        _preferences = preferences;
        CurrentVersion = currentVersion;
        _time = timeProvider ?? TimeProvider.System;
        _downloadFolder = downloadFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlyphLens", "updates");
        _startedAt = _time.GetUtcNow();
        var saved = preferences.Load();
        _automatic = saved.CheckForUpdatesAutomatically;
        LastChecked = saved.LastUpdateCheck;
        State = configuration.IsConfigured ? UpdateState.Idle : UpdateState.NotConfigured;
    }

    public Version CurrentVersion { get; }

    public bool IsConfigured => _configuration.IsConfigured;

    public UpdateState State { get; private set; }

    /// <summary>The newer release found by the last check, if any.</summary>
    public UpdateManifest? Available { get; private set; }

    /// <summary>The verified installer, when <see cref="State"/> is <see cref="UpdateState.ReadyToInstall"/>.</summary>
    public string? Installer { get; private set; }

    /// <summary>Download progress from 0 to 1.</summary>
    public double Progress { get; private set; }

    public string? Problem { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Raised (on any thread) whenever something above changes.</summary>
    public event EventHandler? Changed;

    public bool AutomaticChecks
    {
        get => _automatic;
        set
        {
            if (_automatic == value)
            {
                return;
            }

            _automatic = value;
            _preferences.Update(p => p with { CheckForUpdatesAutomatically = value });
            Raise();
        }
    }

    /// <summary>Starts the background schedule (does nothing when updates are not configured).</summary>
    public void Start()
    {
        if (!IsConfigured)
        {
            return;
        }

        RemoveLeftovers();
        _ = RunScheduleAsync(_stop.Token);
    }

    /// <summary>Asks the update feed now. Returns quietly if a check or download is already running.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            Set(UpdateState.NotConfigured, problem: null);
            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Set(UpdateState.Checking, problem: null);
            byte[] feed = await _client.GetFeedAsync(_configuration.FeedUrl!, cancellationToken).ConfigureAwait(false);
            var result = UpdateFeedReader.Read(feed, _configuration.PublicKeyPem);
            if (result.Manifest is null)
            {
                Fail(result.Problem ?? "The update information was not valid.");
                return;
            }

            var now = _time.GetUtcNow();
            LastChecked = now;
            _preferences.Update(p => p with { LastUpdateCheck = now });
            if (UpdateFeedReader.IsNewer(result.Manifest.Version, CurrentVersion))
            {
                bool sameAsDownloaded = Available?.Version == result.Manifest.Version && Installer is not null && File.Exists(Installer);
                Available = result.Manifest;
                if (!sameAsDownloaded)
                {
                    Installer = null;
                }

                Set(sameAsDownloaded ? UpdateState.ReadyToInstall : UpdateState.Available, problem: null);
            }
            else
            {
                Available = null;
                Installer = null;
                Set(UpdateState.UpToDate, problem: null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
        {
            Fail("The update server could not be reached. GlyphLens will try again later.");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Downloads the available installer and verifies it against the signed manifest.</summary>
    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        var release = Available;
        if (release is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Progress = 0;
            Set(UpdateState.Downloading, problem: null);
            string destination = Path.Combine(_downloadFolder, SafeFileName(release));
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Raise();
            });
            await _client.DownloadAsync(release.DownloadUrl, destination, release.Size, release.Sha256, progress, cancellationToken).ConfigureAwait(false);
            Installer = destination;
            Progress = 1;
            Set(UpdateState.ReadyToInstall, problem: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Fail(ex is InvalidDataException ? ex.Message : "The update could not be downloaded. Try again later.");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// Checks the downloaded installer once more (it may have been changed on disk) and hands it to <paramref name="launch"/>, which
    /// runs it and closes GlyphLens. Returns whether the installer was handed over.
    /// </summary>
    public bool Install(Action<string> launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var release = Available;
        string? installer = Installer;
        if (State != UpdateState.ReadyToInstall || release is null || installer is null)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(installer);
            if (stream.Length != release.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                File.Delete(installer);
                Installer = null;
                Set(UpdateState.Available, "The downloaded installer changed on disk and was deleted. Download it again.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Installer = null;
            Set(UpdateState.Available, "The downloaded installer could not be read. Download it again.");
            return false;
        }

        launch(installer);
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunScheduleAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var due = UpdateSchedule.NextCheckAt(_startedAt, LastChecked, _lastFailure);
                var wait = due - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    // short waits, so turning automatic checks on or off takes effect within a minute
                    await Task.Delay(wait < MaxWait ? wait : MaxWait, _time, stop).ConfigureAwait(false);
                    continue;
                }

                if (_automatic && State is not (UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Checking))
                {
                    await CheckAsync(stop).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(MaxWait, _time, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the application is closing
        }
    }

    private void RemoveLeftovers()
    {
        try
        {
            if (Directory.Exists(_downloadFolder))
            {
                foreach (string partial in Directory.EnumerateFiles(_downloadFolder, "*.partial"))
                {
                    File.Delete(partial);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leftovers are harmless; they are overwritten by the next download
        }
    }

    private static string SafeFileName(UpdateManifest release)
    {
        string name = string.Concat(release.FileName.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        return string.IsNullOrEmpty(name) ? $"GlyphLens-{release.Version}-setup.exe" : name;
    }

    private void Fail(string problem)
    {
        _lastFailure = _time.GetUtcNow();
        Set(UpdateState.Failed, problem);
    }

    private void Set(UpdateState state, string? problem)
    {
        State = state;
        Problem = problem;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
