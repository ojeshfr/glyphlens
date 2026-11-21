using System.IO;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.App.Services;

/// <summary>What <see cref="DesktopShortcutService.EnsureOnce"/> did.</summary>
public enum DesktopShortcutOutcome
{
    /// <summary>Done in an earlier run (also when the user deleted the shortcut since): nothing was touched.</summary>
    AlreadyHandled,

    /// <summary>Turned off with <c>GLYPHLENS_NO_DESKTOP_SHORTCUT=1</c>.</summary>
    Disabled,

    /// <summary>A shortcut named GlyphLens was already on the desktop; it was left as it is.</summary>
    AlreadyExisted,

    /// <summary>The shortcut was created.</summary>
    Created,

    /// <summary>It could not be created; it is tried again at the next start (never twice in one run).</summary>
    Failed,

    /// <summary>Already tried in this run.</summary>
    AlreadyTriedThisRun,
}

/// <summary>
/// Puts a GlyphLens shortcut on the user's desktop the first time the application runs, exactly once. The fact that it was done is
/// remembered in the preferences, so a shortcut the user deleted is not created again; an existing shortcut is left alone. A failure
/// is never shown and never blocks start-up; it is tried again at the next start, at most once per run.
/// </summary>
public sealed class DesktopShortcutService
{
    /// <summary>The shortcut's file name on the desktop.</summary>
    public const string ShortcutFileName = "GlyphLens.lnk";

    /// <summary>Set to <c>1</c> to never create the shortcut (developer builds, tests).</summary>
    public const string DisableEnvironmentVariable = "GLYPHLENS_NO_DESKTOP_SHORTCUT";

    public const string Description = "GlyphLens - offline screen translator";

    private readonly AppPreferencesStore _preferences;
    private readonly IShortcutFiles _shortcuts;
    private readonly Func<string?> _desktopFolder;
    private readonly string _executable;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private int _tried;

    /// <param name="preferences">Where the "done once" flag is kept.</param>
    /// <param name="shortcuts">Writes the shortcut file.</param>
    /// <param name="desktopFolder">The desktop folder (the real one is <c>Environment.SpecialFolder.DesktopDirectory</c>, which follows a
    /// Desktop moved to OneDrive); tests pass a temporary folder.</param>
    /// <param name="executable">The program the shortcut starts (the running <c>GlyphLens.exe</c>).</param>
    /// <param name="getEnvironmentVariable">Reads environment variables (injected for tests).</param>
    public DesktopShortcutService(AppPreferencesStore preferences, IShortcutFiles shortcuts, Func<string?> desktopFolder, string executable, Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(shortcuts);
        ArgumentNullException.ThrowIfNull(desktopFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _preferences = preferences;
        _shortcuts = shortcuts;
        _desktopFolder = desktopFolder;
        _executable = executable;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Creates the shortcut if this is the first run. Blocking (Shell and file calls): call it off the UI thread. Never throws.</summary>
    public DesktopShortcutOutcome EnsureOnce()
    {
        if (_getEnvironmentVariable(DisableEnvironmentVariable) == "1")
        {
            return DesktopShortcutOutcome.Disabled;
        }

        if (_preferences.Load().DesktopShortcutHandled)
        {
            return DesktopShortcutOutcome.AlreadyHandled;
        }

        if (Interlocked.Exchange(ref _tried, 1) != 0)
        {
            return DesktopShortcutOutcome.AlreadyTriedThisRun;
        }

        try
        {
            string? desktop = _desktopFolder();
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                return DesktopShortcutOutcome.Failed;
            }

            string path = Path.Combine(desktop, ShortcutFileName);
            if (File.Exists(path))
            {
                return Remember() ? DesktopShortcutOutcome.AlreadyExisted : DesktopShortcutOutcome.Failed;
            }

            string folder = Path.GetDirectoryName(_executable) ?? string.Empty;
            _shortcuts.Create(path, new ShortcutDefinition(_executable, folder, Description, _executable, 0));
            if (!File.Exists(path))
            {
                return DesktopShortcutOutcome.Failed;
            }

            return Remember() ? DesktopShortcutOutcome.Created : DesktopShortcutOutcome.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return DesktopShortcutOutcome.Failed;
        }
    }

    private bool Remember() => _preferences.Update(p => p with { DesktopShortcutHandled = true });
}
