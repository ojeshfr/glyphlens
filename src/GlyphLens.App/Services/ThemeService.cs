using System.Windows;
using Microsoft.Win32;

namespace GlyphLens.App.Services;

/// <summary>Puts the light or the dark palette into effect.</summary>
public interface IThemeApplier
{
    void Apply(bool dark);
}

/// <summary>Windows' own light/dark setting for apps.</summary>
public interface ISystemTheme
{
    bool PrefersDark { get; }

    /// <summary>Raised when the user may have changed the setting (Windows does not say which preference changed).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Decides between the light and the dark appearance: the user's choice, or Windows' setting while the choice is "System" (and
/// again whenever Windows switches). Applies the result only when it changes.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly IThemeApplier _applier;
    private readonly ISystemTheme _system;
    private bool? _applied;

    public ThemeService(IThemeApplier applier, ISystemTheme system, AppTheme choice = AppTheme.System)
    {
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(system);
        _applier = applier;
        _system = system;
        Choice = choice;
        _system.Changed += OnSystemChanged;
        Refresh();
    }

    public AppTheme Choice { get; private set; }

    /// <summary>Whether the dark palette is in effect.</summary>
    public bool IsDark => _applied == true;

    /// <summary>Raised after the appearance in effect, or the choice, changed.</summary>
    public event EventHandler? Changed;

    public void SetChoice(AppTheme choice)
    {
        if (!Enum.IsDefined(choice))
        {
            throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown theme.");
        }

        bool choiceChanged = choice != Choice;
        Choice = choice;
        if (!Refresh() && choiceChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _system.Changed -= OnSystemChanged;

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        if (Choice == AppTheme.System)
        {
            Refresh();
        }
    }

    /// <summary>Applies the appearance the choice calls for; returns whether anything was applied.</summary>
    private bool Refresh()
    {
        bool dark = Choice switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => _system.PrefersDark,
        };
        if (_applied == dark)
        {
            return false;
        }

        _applier.Apply(dark);
        _applied = dark;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>Reads Windows' "Choose your app mode" setting and reports when the user's preferences change.</summary>
public sealed class WindowsSystemTheme : ISystemTheme, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public WindowsSystemTheme()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler? Changed;

    public bool PrefersDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                return false;
            }
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Swaps GlyphLens' palette dictionary (Palette.Light.xaml / Palette.Dark.xaml) and the Fluent theme's mode. Every view refers to the
/// palette's brushes with DynamicResource, so open windows switch at once.
/// </summary>
public sealed class WpfThemeApplier : IThemeApplier
{
    public void Apply(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        // ThemeMode is marked experimental (WPF0001, suppressed in the project file); it is how the Fluent theme is switched.
        application.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var dictionaries = application.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/GlyphLens;component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        for (int i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Source?.OriginalString.Contains("Palette.", StringComparison.Ordinal) == true)
            {
                dictionaries[i] = palette;
                return;
            }
        }

        dictionaries.Insert(0, palette);
    }
}
