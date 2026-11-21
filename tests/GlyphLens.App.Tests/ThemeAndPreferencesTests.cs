using GlyphLens.App.Services;
using GlyphLens.App.ViewModels;

namespace GlyphLens.App.Tests;

public sealed class ThemeAndPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-prefs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeApplier : IThemeApplier
    {
        public List<bool> Applied { get; } = [];

        public void Apply(bool dark) => Applied.Add(dark);
    }

    private sealed class FakeSystem : ISystemTheme
    {
        public bool PrefersDark { get; set; }

        public event EventHandler? Changed;

        public void Switch(bool dark)
        {
            PrefersDark = dark;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private AppPreferencesStore Store() => new(Path.Combine(_folder, "preferences.json"));

    [Fact]
    public void FollowingTheSystem_AppliesWindowsSetting_AndSwitchesWithIt()
    {
        var applier = new FakeApplier();
        var system = new FakeSystem { PrefersDark = true };
        using var theme = new ThemeService(applier, system, AppTheme.System);

        Assert.True(theme.IsDark);
        system.Switch(dark: false);

        Assert.False(theme.IsDark);
        Assert.Equal([true, false], applier.Applied);
    }

    [Fact]
    public void AFixedChoice_IgnoresWindowsSetting()
    {
        var applier = new FakeApplier();
        var system = new FakeSystem { PrefersDark = false };
        using var theme = new ThemeService(applier, system, AppTheme.Dark);

        system.Switch(dark: true);
        system.Switch(dark: false);

        Assert.True(theme.IsDark);
        Assert.Equal([true], applier.Applied);
    }

    [Fact]
    public void ThePaletteIsAppliedOnlyWhenItChanges()
    {
        var applier = new FakeApplier();
        using var theme = new ThemeService(applier, new FakeSystem { PrefersDark = false }, AppTheme.System);

        theme.SetChoice(AppTheme.Light); // still light: nothing to apply
        theme.SetChoice(AppTheme.Dark);

        Assert.Equal([false, true], applier.Applied);
        Assert.Equal(AppTheme.Dark, theme.Choice);
    }

    [Fact]
    public void Preferences_AreRemembered_AndDefaultToSystemAndAutomaticUpdateChecks()
    {
        var store = Store();
        Assert.Equal(AppTheme.System, store.Load().Theme);
        Assert.True(store.Load().CheckForUpdatesAutomatically);

        Assert.True(store.Update(p => p with { Theme = AppTheme.Dark, CheckForUpdatesAutomatically = false }));

        var reloaded = Store().Load();
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
        Assert.False(reloaded.CheckForUpdatesAutomatically);
        Assert.Contains("\"dark\"", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"theme\":\"Purple\"}")]
    [InlineData("{\"version\":99,\"theme\":\"Dark\"}")]
    public void ABrokenOrForeignPreferencesFile_GivesTheDefaults(string content)
    {
        Directory.CreateDirectory(_folder);
        var store = Store();
        File.WriteAllText(store.FilePath, content);

        Assert.Equal(new AppPreferences(), store.Load());
    }

    [Fact]
    public void TheAppearanceSetting_AppliesAndRemembersTheChoice_AndDescribesIt()
    {
        var applier = new FakeApplier();
        var store = Store();
        using var theme = new ThemeService(applier, new FakeSystem { PrefersDark = true }, AppTheme.System);
        using var appearance = new AppearanceViewModel(theme, store);
        var changed = new List<string?>();
        appearance.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(appearance.IsSystem);
        Assert.Contains("dark mode now", appearance.Description, StringComparison.Ordinal);

        appearance.IsLight = true;

        Assert.True(appearance.IsLight);
        Assert.False(appearance.IsSystem);
        Assert.False(theme.IsDark);
        Assert.Equal(AppTheme.Light, store.Load().Theme);
        Assert.Contains(nameof(AppearanceViewModel.IsSystem), changed);
        Assert.Equal("Always light.", appearance.Description);
    }

    [Fact]
    public void ARadioButtonTurningOff_IsNotAChoice()
    {
        using var theme = new ThemeService(new FakeApplier(), new FakeSystem(), AppTheme.Dark);
        using var appearance = new AppearanceViewModel(theme, Store());

        appearance.IsDark = false;

        Assert.Equal(AppTheme.Dark, theme.Choice);
    }
}
