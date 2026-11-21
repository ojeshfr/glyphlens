using System.IO;
using System.Text.Json;

namespace GlyphLens.App.Overlay;

/// <summary>
/// What is remembered about the overlay between sessions: only its layout. Never any text, translation or capture.
/// </summary>
public sealed record OverlaySettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The saved window rectangle in physical pixels, or <see langword="null"/> when never moved.</summary>
    public int? X { get; init; }

    public int? Y { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double Opacity { get; init; } = 1.0;

    public bool Pinned { get; init; } = true;

    public bool Collapsed { get; init; }

    /// <summary>The size of the card text in DIPs (11 to 20).</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>Largest width the user allows, in physical pixels (0 = no extra limit).</summary>
    public int MaxWidth { get; init; }

    public PixelRect? Rect => X is { } x && Y is { } y && Width is { } w && Height is { } h && w > 0 && h > 0 ? new PixelRect(x, y, w, h) : null;
}

/// <summary>Reads and writes <see cref="OverlaySettings"/> as a small JSON file in the user's local application data.</summary>
public sealed class OverlaySettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;

    public OverlaySettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlyphLens", "overlay.json");
    }

    public string FilePath => _path;

    /// <summary>The saved settings, or the defaults when there are none or the file cannot be read (never throws).</summary>
    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 16 * 1024)
            {
                return new OverlaySettings();
            }

            var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(_path), Options);
            return settings is null || settings.Version != OverlaySettings.CurrentVersion ? new OverlaySettings() : Sanitize(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new OverlaySettings();
        }
    }

    /// <summary>Saves atomically (temporary file, then replace). A failure is swallowed: remembering the position is a convenience.</summary>
    public bool Save(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Sanitize(settings), Options));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static OverlaySettings Sanitize(OverlaySettings s) => s with
    {
        Opacity = double.IsNaN(s.Opacity) ? 1.0 : Math.Clamp(s.Opacity, 0.35, 1.0),
        FontSize = double.IsNaN(s.FontSize) ? 13 : Math.Clamp(s.FontSize, 11, 20),
        MaxWidth = Math.Max(0, s.MaxWidth),
    };
}
