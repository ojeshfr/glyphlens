using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphLens.App.Services;

/// <summary>The appearance the user chose: follow Windows, or always light, or always dark.</summary>
public enum AppTheme
{
    /// <summary>Follow Windows' "app mode" (light or dark) and switch with it.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// The application-wide preferences remembered between sessions: the appearance, the update settings and whether the desktop shortcut
/// was created once. Never any screen text,
/// translation or capture (the overlay's layout is kept separately, see <see cref="Overlay.OverlaySettingsStore"/>).
/// </summary>
public sealed record AppPreferences
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Whether GlyphLens looks for updates on its own (at start-up and about once a day). On by default.</summary>
    public bool CheckForUpdatesAutomatically { get; init; } = true;

    /// <summary>When the update server was last asked successfully (UTC), or <see langword="null"/> if never.</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>
    /// Whether the desktop shortcut was created (or found) once. Set once, so a shortcut the user deleted is never created again.
    /// </summary>
    public bool DesktopShortcutHandled { get; init; }
}

/// <summary>Reads and writes <see cref="AppPreferences"/> as a small JSON file in the user's local application data.</summary>
public sealed class AppPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly object _gate = new();

    public AppPreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlyphLens", "preferences.json");
    }

    public string FilePath => _path;

    /// <summary>The saved preferences, or the defaults when there are none or the file cannot be read (never throws).</summary>
    public AppPreferences Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length > 16 * 1024)
                {
                    return new AppPreferences();
                }

                var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_path), Options);
                if (preferences is null || preferences.Version != AppPreferences.CurrentVersion || !Enum.IsDefined(preferences.Theme))
                {
                    return new AppPreferences();
                }

                return preferences;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new AppPreferences();
            }
        }
    }

    /// <summary>Changes the saved preferences (read, change, write atomically). A failure is swallowed: these are conveniences.</summary>
    public bool Update(Func<AppPreferences, AppPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            try
            {
                var next = change(Load());
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
                File.Move(temporary, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
