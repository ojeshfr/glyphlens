using GlyphLens.App.Services;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

public sealed class WorkerEnvironmentSwitchTests
{
    private const string Managed = @"C:\Users\u\AppData\Local\GlyphLens\runtime\venv\Scripts\python.exe";

    private sealed class Launcher(string? current)
    {
        public string? Current { get; set; } = current;

        public int Forgotten { get; private set; }

        public void Forget()
        {
            Forgotten++;
            Current = Managed; // the next start finds the managed environment first
        }
    }

    [Fact]
    public async Task AWorkerRunningOnAnotherPython_IsRestartedOntoTheManagedOne()
    {
        var worker = new FakeWorkerManager();
        await worker.StartAsync(CancellationToken.None);
        var launcher = new Launcher(@"C:\Python314\python.exe");

        var result = await new WorkerEnvironmentSwitch(worker, () => launcher.Current, launcher.Forget).UseInterpreterAsync(Managed, false, CancellationToken.None);

        Assert.True(result.Restarted);
        Assert.Equal(Managed, result.InterpreterPath);
        Assert.Equal(1, launcher.Forgotten);
        Assert.Equal(1, worker.RestartCalls);
    }

    [Fact]
    public async Task AWorkerAlreadyOnTheManagedPython_IsLeftAlone_UnlessTheEnvironmentChanged()
    {
        var worker = new FakeWorkerManager();
        await worker.StartAsync(CancellationToken.None);
        var launcher = new Launcher(Managed);
        var sut = new WorkerEnvironmentSwitch(worker, () => launcher.Current, launcher.Forget);

        var same = await sut.UseInterpreterAsync(Managed.ToUpperInvariant(), false, CancellationToken.None);
        Assert.False(same.Restarted);
        Assert.Equal(0, worker.RestartCalls);

        var changed = await sut.UseInterpreterAsync(Managed, true, CancellationToken.None);
        Assert.True(changed.Restarted);
        Assert.Equal(1, worker.RestartCalls);
    }

    [Fact]
    public async Task AStoppedOrUnavailableWorker_IsStarted()
    {
        var worker = new FakeWorkerManager();
        worker.BecomeUnavailable("No Python was found.");
        var launcher = new Launcher(null);

        var result = await new WorkerEnvironmentSwitch(worker, () => launcher.Current, launcher.Forget).UseInterpreterAsync(Managed, false, CancellationToken.None);

        Assert.True(result.Restarted);
        Assert.Equal(1, worker.StartCalls);
        Assert.Equal(WorkerStatus.Ready, worker.Status);
    }
}

public sealed class DesktopShortcutServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-shortcut-" + Guid.NewGuid().ToString("N"));

    public DesktopShortcutServiceTests()
    {
        Directory.CreateDirectory(Desktop);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Desktop => Path.Combine(_folder, "Desktop");

    private string Shortcut => Path.Combine(Desktop, "GlyphLens.lnk");

    private const string Exe = @"C:\Apps\GlyphLens\GlyphLens.exe";

    private AppPreferencesStore Preferences => new(Path.Combine(_folder, "preferences.json"));

    private sealed class FakeShortcuts : IShortcutFiles
    {
        public List<(string Path, ShortcutDefinition Definition)> Created { get; } = [];

        public bool Fail { get; set; }

        public void Create(string shortcutPath, ShortcutDefinition definition)
        {
            if (Fail)
            {
                throw new IOException("the Shell refused");
            }

            File.WriteAllText(shortcutPath, "lnk");
            Created.Add((shortcutPath, definition));
        }

        public ShortcutDefinition Read(string shortcutPath) => Created.Last(c => c.Path == shortcutPath).Definition;
    }

    private DesktopShortcutService Service(FakeShortcuts shortcuts, string? disabled = null) =>
        new(Preferences, shortcuts, () => Desktop, Exe, name => name == DesktopShortcutService.DisableEnvironmentVariable ? disabled : null);

    [Fact]
    public void TheFirstRun_CreatesTheShortcut_PointingAtTheProgram_AndRemembersIt()
    {
        var shortcuts = new FakeShortcuts();

        var outcome = Service(shortcuts).EnsureOnce();

        Assert.Equal(DesktopShortcutOutcome.Created, outcome);
        var (path, definition) = Assert.Single(shortcuts.Created);
        Assert.Equal(Shortcut, path);
        Assert.Equal(Exe, definition.TargetPath);
        Assert.Equal(@"C:\Apps\GlyphLens", definition.WorkingDirectory);
        Assert.Equal("GlyphLens - offline screen translator", definition.Description);
        Assert.Equal(Exe, definition.IconPath);
        Assert.True(Preferences.Load().DesktopShortcutHandled);
    }

    [Fact]
    public void AShortcutTheUserDeleted_IsNotCreatedAgain()
    {
        var shortcuts = new FakeShortcuts();
        Service(shortcuts).EnsureOnce();
        File.Delete(Shortcut);

        var outcome = Service(shortcuts).EnsureOnce(); // a later start

        Assert.Equal(DesktopShortcutOutcome.AlreadyHandled, outcome);
        Assert.Single(shortcuts.Created);
        Assert.False(File.Exists(Shortcut));
    }

    [Fact]
    public void AnExistingShortcut_IsLeftAlone_AndRemembered()
    {
        File.WriteAllText(Shortcut, "the user's own");
        var shortcuts = new FakeShortcuts();

        var outcome = Service(shortcuts).EnsureOnce();

        Assert.Equal(DesktopShortcutOutcome.AlreadyExisted, outcome);
        Assert.Empty(shortcuts.Created);
        Assert.Equal("the user's own", File.ReadAllText(Shortcut));
        Assert.True(Preferences.Load().DesktopShortcutHandled);
    }

    [Fact]
    public void AFailure_IsNotRemembered_IsNotRetriedInTheSameRun_ButIsTriedAtTheNextStart()
    {
        var shortcuts = new FakeShortcuts { Fail = true };
        var service = Service(shortcuts);

        Assert.Equal(DesktopShortcutOutcome.Failed, service.EnsureOnce());
        Assert.Equal(DesktopShortcutOutcome.AlreadyTriedThisRun, service.EnsureOnce());
        Assert.False(Preferences.Load().DesktopShortcutHandled);

        shortcuts.Fail = false;
        Assert.Equal(DesktopShortcutOutcome.Created, Service(shortcuts).EnsureOnce());
    }

    [Fact]
    public void AMissingDesktopFolder_IsAQuietFailure()
    {
        Directory.Delete(Desktop);

        Assert.Equal(DesktopShortcutOutcome.Failed, Service(new FakeShortcuts()).EnsureOnce());
        Assert.False(Preferences.Load().DesktopShortcutHandled);
    }

    [Fact]
    public void TheEnvironmentSwitch_TurnsItOff()
    {
        var shortcuts = new FakeShortcuts();

        Assert.Equal(DesktopShortcutOutcome.Disabled, Service(shortcuts, disabled: "1").EnsureOnce());
        Assert.Empty(shortcuts.Created);
    }

    [Fact]
    public void ThePreference_SurvivesARoundTrip_AndOldFilesWithoutItMeanNotYet()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "preferences.json"), "{ \"version\": 1, \"theme\": \"Dark\" }");
        Assert.False(Preferences.Load().DesktopShortcutHandled);

        Assert.True(Preferences.Update(p => p with { DesktopShortcutHandled = true }));

        var loaded = Preferences.Load();
        Assert.True(loaded.DesktopShortcutHandled);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
    }
}
