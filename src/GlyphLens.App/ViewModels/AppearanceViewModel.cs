using GlyphLens.App.Mvvm;
using GlyphLens.App.Services;

namespace GlyphLens.App.ViewModels;

/// <summary>The appearance setting (System / Light / Dark) for the Settings page: applies the choice at once and remembers it.</summary>
public sealed class AppearanceViewModel : ViewModelBase, IDisposable
{
    private readonly ThemeService _theme;
    private readonly AppPreferencesStore? _store;

    public AppearanceViewModel(ThemeService theme, AppPreferencesStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        _store = store;
        _theme.Changed += OnThemeChanged;
    }

    public bool IsSystem
    {
        get => _theme.Choice == AppTheme.System;
        set => Choose(value, AppTheme.System);
    }

    public bool IsLight
    {
        get => _theme.Choice == AppTheme.Light;
        set => Choose(value, AppTheme.Light);
    }

    public bool IsDark
    {
        get => _theme.Choice == AppTheme.Dark;
        set => Choose(value, AppTheme.Dark);
    }

    /// <summary>What is in effect, in words.</summary>
    public string Description => _theme.Choice switch
    {
        AppTheme.System => _theme.IsDark ? "Following Windows, which is in dark mode now." : "Following Windows, which is in light mode now.",
        AppTheme.Dark => "Always dark.",
        _ => "Always light.",
    };

    public void Dispose() => _theme.Changed -= OnThemeChanged;

    private void Choose(bool selected, AppTheme theme)
    {
        // Radio buttons also report the button that was turned off; only the one turned on is a choice.
        if (!selected || _theme.Choice == theme)
        {
            return;
        }

        _theme.SetChoice(theme);
        _store?.Update(p => p with { Theme = theme });
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(Description));
    }
}
