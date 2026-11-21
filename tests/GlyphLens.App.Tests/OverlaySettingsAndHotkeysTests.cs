using GlyphLens.App.Overlay;

namespace GlyphLens.App.Tests;

public sealed class OverlaySettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glyphlens-overlay-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "overlay.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void WithNoFile_TheDefaultsAreReturned()
    {
        var settings = new OverlaySettingsStore(FilePath).Load();

        Assert.Null(settings.Rect);
        Assert.Equal(1.0, settings.Opacity);
        Assert.True(settings.Pinned);
        Assert.False(settings.Collapsed);
    }

    [Fact]
    public void SavedSettings_AreReadBack()
    {
        var store = new OverlaySettingsStore(FilePath);
        var saved = new OverlaySettings { X = -1500, Y = 120, Width = 600, Height = 840, Opacity = 0.8, Pinned = false, Collapsed = true, MaxWidth = 900 };

        Assert.True(store.Save(saved));
        var loaded = store.Load();

        Assert.Equal(saved, loaded);
        Assert.Equal(new PixelRect(-1500, 120, 600, 840), loaded.Rect);
    }

    [Fact]
    public void ACorruptFile_FallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        Assert.Null(new OverlaySettingsStore(FilePath).Load().Rect);
    }

    [Fact]
    public void AFileFromAnotherVersion_IsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"version": 99, "x": 5, "y": 5, "width": 400, "height": 500}""");

        Assert.Null(new OverlaySettingsStore(FilePath).Load().Rect);
    }

    [Fact]
    public void AnOversizedFile_IsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, new string(' ', 20_000));

        Assert.Null(new OverlaySettingsStore(FilePath).Load().Rect);
    }

    [Fact]
    public void OutOfRangeValues_AreCorrectedOnLoad()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"version": 1, "opacity": 0.01, "maxWidth": -5}""");

        var settings = new OverlaySettingsStore(FilePath).Load();

        Assert.Equal(0.35, settings.Opacity);
        Assert.Equal(0, settings.MaxWidth);
    }

    [Fact]
    public void ARectWithoutASize_IsNotAPosition()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"version": 1, "x": 10, "y": 10, "width": 0, "height": 0}""");

        Assert.Null(new OverlaySettingsStore(FilePath).Load().Rect);
    }

    [Fact]
    public void TheFileHoldsOnlyLayout_NeverTextOrTranslations()
    {
        var store = new OverlaySettingsStore(FilePath);
        store.Save(new OverlaySettings { X = 1, Y = 2, Width = 400, Height = 560 });

        var json = File.ReadAllText(FilePath);

        Assert.DoesNotContain("text", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("translat", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void AFailedSave_IsReportedAndDoesNotThrow()
    {
        var blocked = Path.Combine(_directory, "file-as-directory");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "x");

        Assert.False(new OverlaySettingsStore(Path.Combine(blocked, "overlay.json")).Save(new OverlaySettings()));
    }
}

public sealed class GlobalHotkeyServiceTests
{
    private sealed class FakeNative : IHotkeyNative
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];

        public HashSet<(uint Modifiers, uint Key)> TakenByOthers { get; } = [];

        public bool Disposed { get; private set; }

        public event EventHandler<int>? HotkeyPressed;

        public bool Register(int id, uint modifiers, uint virtualKey)
        {
            if (TakenByOthers.Contains((modifiers, virtualKey)))
            {
                return false;
            }

            Registered[id] = (modifiers, virtualKey);
            return true;
        }

        public void Unregister(int id) => Registered.Remove(id);

        public void Press(int id) => HotkeyPressed?.Invoke(this, id);

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TheDefaultShortcuts_AreCtrlAltTOPICWithNoRepeat()
    {
        var bindings = GlobalHotkeyService.DefaultBindings;

        Assert.Equal(
            ["Ctrl+Alt+T", "Ctrl+Alt+O", "Ctrl+Alt+P", "Ctrl+Alt+I", "Ctrl+Alt+C"],
            bindings.Select(b => b.Display));
        Assert.Equal(
            [OverlayHotkeyAction.ToggleTranslation, OverlayHotkeyAction.ToggleOverlay, OverlayHotkeyAction.TogglePause, OverlayHotkeyAction.ToggleInteractive, OverlayHotkeyAction.ClearResults],
            bindings.Select(b => b.Action));
        Assert.All(bindings, b => Assert.Equal(GlobalHotkeyService.ModControl | GlobalHotkeyService.ModAlt | GlobalHotkeyService.ModNoRepeat, b.Modifiers));
        Assert.Equal(['T', 'O', 'P', 'I', 'C'], bindings.Select(b => (char)b.VirtualKey));
    }

    [Fact]
    public void RegisterAll_RegistersEveryShortcutWithItsOwnId()
    {
        var native = new FakeNative();
        using var service = new GlobalHotkeyService(native);

        var results = service.RegisterAll();

        Assert.All(results, r => Assert.True(r.Registered));
        Assert.Equal(5, native.Registered.Count);
        Assert.Equal(5, native.Registered.Keys.Distinct().Count());
    }

    [Fact]
    public void AShortcutAnotherProgramOwns_IsReported_AndTheOthersStillWork()
    {
        var native = new FakeNative();
        var overlay = GlobalHotkeyService.DefaultBindings.Single(b => b.Action == OverlayHotkeyAction.ToggleOverlay);
        native.TakenByOthers.Add((overlay.Modifiers, overlay.VirtualKey));
        using var service = new GlobalHotkeyService(native);
        var pressed = new List<OverlayHotkeyAction>();
        service.ActionRequested += (_, a) => pressed.Add(a);

        var results = service.RegisterAll();

        var conflict = Assert.Single(results, r => !r.Registered);
        Assert.Equal(OverlayHotkeyAction.ToggleOverlay, conflict.Binding.Action);
        Assert.Contains("Ctrl+Alt+O", conflict.Problem, StringComparison.Ordinal);
        Assert.Contains("already used", conflict.Problem, StringComparison.Ordinal);
        Assert.Equal(4, native.Registered.Count);
        native.Press(native.Registered.Keys.First());
        Assert.Single(pressed);
    }

    [Fact]
    public void APressedShortcut_RaisesItsAction_AndAnUnknownIdIsIgnored()
    {
        var native = new FakeNative();
        using var service = new GlobalHotkeyService(native);
        service.RegisterAll();
        var pressed = new List<OverlayHotkeyAction>();
        service.ActionRequested += (_, a) => pressed.Add(a);
        var idOfTranslation = native.Registered.First(p => p.Value.Key == 'T').Key;

        native.Press(idOfTranslation);
        native.Press(12345);

        Assert.Equal([OverlayHotkeyAction.ToggleTranslation], pressed);
    }

    [Fact]
    public void Dispose_UnregistersEverything_AndStopsListening()
    {
        var native = new FakeNative();
        var service = new GlobalHotkeyService(native);
        service.RegisterAll();
        var pressed = 0;
        service.ActionRequested += (_, _) => pressed++;
        int id = native.Registered.Keys.First();

        service.Dispose();
        service.Dispose();
        native.Press(id);

        Assert.Empty(native.Registered);
        Assert.True(native.Disposed);
        Assert.Equal(0, pressed);
        Assert.Throws<ObjectDisposedException>(() => service.RegisterAll());
    }

    [Fact]
    public void CustomBindingsCanBeRegistered()
    {
        var native = new FakeNative();
        using var service = new GlobalHotkeyService(native);

        var results = service.RegisterAll([new HotkeyBinding(OverlayHotkeyAction.ClearResults, GlobalHotkeyService.ModControl, 'X', "Ctrl+X")]);

        Assert.Single(results);
        Assert.Single(native.Registered);
    }

    [Fact]
    public void Constructor_RejectsNull() => Assert.Throws<ArgumentNullException>(() => new GlobalHotkeyService(null!));
}
