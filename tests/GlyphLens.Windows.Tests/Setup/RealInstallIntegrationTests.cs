using System.Diagnostics;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;
using GlyphLens.Core.Setup;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Infrastructure.Setup;
using GlyphLens.Infrastructure.Translation;
using GlyphLens.Windows.Setup;
using GlyphLens.Windows.Worker;
using Xunit.Abstractions;

namespace GlyphLens.Windows.Tests.Setup;

/// <summary>
/// Runs only when <c>GLYPHLENS_RUN_INSTALL_TESTS=1</c>: these tests download about 360 MB from the internet (uv, a Python build,
/// the locked packages, the OCR and translation models) and install them, so CI never runs them.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InstallTestFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "GLYPHLENS_RUN_INSTALL_TESTS";

    public InstallTestFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Downloads and installs the real environment (about 360 MB, needs the internet). Set {EnvironmentVariable}=1 to run.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealInstallTestGroup
{
    public const string Name = "Real install (opt-in, not parallel)";
}

/// <summary>
/// The real one-click setup, end to end, with the production classes (pinned downloads over the network, uv in job objects, the real
/// worker and its validated model importer) into folders outside the user's profile:
/// <c>GLYPHLENS_INSTALL_TEST_ROOT</c> (default <c>%TEMP%\glyphlens-install-test</c>) holds <c>runtime</c> and <c>models</c>; the folders
/// are kept after the run so the result can be inspected and the second run can prove that nothing is downloaded again.
/// </summary>
[Collection(RealInstallTestGroup.Name)]
public sealed class RealInstallIntegrationTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable("GLYPHLENS_INSTALL_TEST_ROOT") is { Length: > 0 } root
        ? root
        : Path.Combine(Path.GetTempPath(), "glyphlens-install-test");

    [InstallTestFact]
    public async Task TheRealSetup_InstallsEverything_ThenASecondRunSkipsEverything_AndTranslationWorks()
    {
        string runtime = Path.Combine(Root, "runtime");
        string models = Path.Combine(Root, "models");

        var first = await RunAsync(runtime, models);
        output.WriteLine($"First run: {first.Elapsed.TotalSeconds:0.0} s, {first.Downloads.Downloaded} file(s) downloaded ({first.Downloads.Bytes:N0} bytes by GlyphLens' own downloader), skipped: {string.Join(", ", first.Result.Skipped)}.");
        Assert.Empty(first.Result.Warnings);

        var second = await RunAsync(runtime, models, translate: true);
        output.WriteLine($"Second run: {second.Elapsed.TotalSeconds:0.0} s, {second.Downloads.Downloaded} file(s) downloaded, skipped: {string.Join(", ", second.Result.Skipped)}.");
        Assert.Equal(0, second.Downloads.Downloaded);
        Assert.Equal(
            [SetupStepId.Uv, SetupStepId.Python, SetupStepId.Packages, SetupStepId.OcrModels, SetupStepId.Worker, SetupStepId.SimplifiedModel, SetupStepId.TraditionalModel],
            second.Result.Skipped);
        Assert.True(second.Elapsed < TimeSpan.FromSeconds(90), $"The second run took {second.Elapsed}.");
        Assert.Empty(Directory.EnumerateFiles(Root, "*" + HttpFileDownloader.PartialSuffix, SearchOption.AllDirectories));
    }

    [InstallTestFact]
    public async Task CancellingTheRealSetup_KillsUv_AndLeavesNoPartialFiles()
    {
        string runtime = Path.Combine(Root, "cancel-runtime");
        string models = Path.Combine(Root, "cancel-models");

        // 1. Cancel in the middle of the first download (uv's archive): the partial file must be gone.
        using (var cts = new CancellationTokenSource())
        {
            var progress = new InlineProgress(p =>
            {
                if (p.Step == SetupStepId.Uv && p.BytesDone is > 1_000_000)
                {
                    cts.Cancel();
                }
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCancellableAsync(runtime, models, progress, cts.Token));
            Assert.Empty(Directory.EnumerateFiles(runtime, "*" + HttpFileDownloader.PartialSuffix, SearchOption.AllDirectories));
            output.WriteLine("Cancelled during the uv download: no partial file left.");
        }

        // 2. Cancel while uv installs the packages (a child process tree is running): nothing from the runtime folder may keep running.
        using (var cts = new CancellationTokenSource())
        {
            var progress = new InlineProgress(p =>
            {
                if (p.Step == SetupStepId.Packages && p.State == SetupStepState.Running)
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                }
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCancellableAsync(runtime, models, progress, cts.Token));
            await Task.Delay(TimeSpan.FromSeconds(2));
            var survivors = ProcessesUnder(runtime);
            Assert.True(survivors.Count == 0, "Still running: " + string.Join(", ", survivors));
            Assert.Empty(Directory.EnumerateFiles(runtime, "*" + HttpFileDownloader.PartialSuffix, SearchOption.AllDirectories));
            Assert.False(File.Exists(RuntimeDirectory.LockMarker(runtime)), "A cancelled package install must not be marked as complete.");
            output.WriteLine("Cancelled during the package install: no process from the runtime folder is running, no partial file left, no completion marker.");
        }
    }

    [InstallTestFact]
    public async Task ARealDownloadWithAWrongPinnedHash_IsRefused_WithTheServerChangedMessage_AndNothingIsKept()
    {
        string folder = Path.Combine(Root, "hash-check");
        Directory.CreateDirectory(folder);
        var real = SetupManifest.OcrFiles.Single(f => f.File.Id == "ocr-det-yml").File; // 903 bytes
        var tampered = real with { Sha256 = new string('0', 64) };
        string destination = Path.Combine(folder, "inference.yml");
        using var downloader = new HttpFileDownloader("0.0.0-test");

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(tampered, destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.HashMismatch, ex.Kind);
        Assert.Contains("The file on the server changed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("do not use it", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + HttpFileDownloader.PartialSuffix));

        // The same file with the right pin downloads and verifies.
        Assert.True(await downloader.DownloadAsync(real, destination, null, CancellationToken.None));
        Assert.True(await HttpFileDownloader.VerifiesAsync(destination, real, CancellationToken.None));
        output.WriteLine("Wrong pin refused with: " + ex.Message);
    }

    private sealed record RunOutcome(SetupResult Result, TimeSpan Elapsed, CountingDownloader Downloads);

    private async Task<RunOutcome> RunAsync(string runtime, string models, bool translate = false)
    {
        var watch = Stopwatch.StartNew();
        await using var rig = await Rig.CreateAsync(runtime, models, output);
        if (File.Exists(RuntimeDirectory.VirtualEnvironmentPython(runtime)))
        {
            // As in the application, where the worker is usually already running when the user starts the setup again.
            await rig.Worker.StartAsync(CancellationToken.None);
        }

        var result = await rig.Installer.RunAsync(new SetupOptions(IncludeTraditional: true), new InlineProgress(p => output.WriteLine($"{p.Step} {p.State}: {p.Message}")), CancellationToken.None);
        watch.Stop();

        var health = await rig.Worker.GetHealthAsync(CancellationToken.None);
        output.WriteLine($"Worker: Python {health.PythonVersion}, OCR {health.Ocr.ToWireString()}, translation {health.Translation.ToWireString()}.");
        Assert.Equal(LocalOcrState.Ready, LocalOcrAdvisor.Classify(health));
        Assert.Contains(health.Translation, new[] { WorkerComponentState.Installed, WorkerComponentState.Ready });
        Assert.StartsWith("3.12", health.PythonVersion, StringComparison.Ordinal);
        var catalog = await rig.Catalog.GetCatalogAsync(CancellationToken.None);
        Assert.Contains(catalog.Packages, p => p.From == "zh" && p.To == "en");
        Assert.Contains(catalog.Packages, p => p.From == "zt" && p.To == "en");

        if (translate)
        {
            using var engine = new WorkerTranslationEngine(rig.Worker);
            var translated = await engine.TranslateAsync(new TranslationRequest("今天天气很好，我们去公园散步吧。", "zh", "en"), CancellationToken.None);
            output.WriteLine($"zh -> en: {translated.TranslatedText}");
            Assert.Matches("[A-Za-z]{3,}", translated.TranslatedText);
        }

        return new RunOutcome(result, watch.Elapsed, rig.Downloads);
    }

    private async Task RunCancellableAsync(string runtime, string models, IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        await using var rig = await Rig.CreateAsync(runtime, models, output);
        _ = await rig.Installer.RunAsync(new SetupOptions(IncludeTraditional: false), progress, cancellationToken);
    }

    /// <summary>Processes whose executable lies inside <paramref name="folder"/> (uv, the Python it installed).</summary>
    private static List<string> ProcessesUnder(string folder)
    {
        string prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add($"{process.ProcessName} ({process.Id})");
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // other users' or protected processes
                }
            }
        }

        return found;
    }

    /// <summary>The production classes wired as the application wires them, but with explicit folders and the test's own worker.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string? _previousModelsDirectory;
        private readonly PythonWorkerProcessLauncher _launcher;
        private readonly HttpFileDownloader _downloader;

        private Rig(string runtime, string models, ITestOutputHelper output)
        {
            // The worker reads its models folder from the environment it inherits; restored in DisposeAsync (this collection runs alone).
            _previousModelsDirectory = Environment.GetEnvironmentVariable(ModelsDirectory.EnvironmentVariable);
            Environment.SetEnvironmentVariable(ModelsDirectory.EnvironmentVariable, models);
            _launcher = new PythonWorkerProcessLauncher(new PythonWorkerLaunchOptions
            {
                InterpreterPath = RuntimeDirectory.VirtualEnvironmentPython(runtime),
                WorkerDirectory = WorkerLocator.FindWorkerDirectory(),
            });
            Worker = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), _launcher);
            Catalog = new WorkerModelCatalog(Worker);
            _downloader = new HttpFileDownloader("0.0.0-test");
            Downloads = new CountingDownloader(_downloader);
            var log = new SetupLogFile(RuntimeDirectory.LogFile(runtime));
            Installer = new EnvironmentInstaller(new EnvironmentInstallerServices(
                new RuntimeLocation(runtime, null),
                models,
                Downloads,
                new JobProcessRunner(),
                Catalog,
                Worker,
                new TestWorkerSwitch(Worker, () => _launcher.InterpreterPath),
                new EchoLog(log, output),
                EngineLock.LoadEmbedded()));
        }

        public WorkerProcessManager Worker { get; }

        public WorkerModelCatalog Catalog { get; }

        public CountingDownloader Downloads { get; }

        public EnvironmentInstaller Installer { get; }

        public static Task<Rig> CreateAsync(string runtime, string models, ITestOutputHelper output) => Task.FromResult(new Rig(runtime, models, output));

        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync();
            _launcher.Dispose();
            _downloader.Dispose();
            Environment.SetEnvironmentVariable(ModelsDirectory.EnvironmentVariable, _previousModelsDirectory);
        }
    }

    /// <summary>Starts or restarts the test's worker (which always uses the installed environment's interpreter).</summary>
    private sealed class TestWorkerSwitch(IWorkerProcessManager worker, Func<string?> interpreter) : IWorkerEnvironmentSwitch
    {
        public async Task<WorkerSwitchResult> UseInterpreterAsync(string pythonPath, bool restart, CancellationToken cancellationToken)
        {
            bool restarted = restart || worker.Status != WorkerStatus.Ready;
            if (worker.Status == WorkerStatus.Ready)
            {
                if (restart)
                {
                    await worker.RestartAsync(cancellationToken);
                }
            }
            else
            {
                await worker.StartAsync(cancellationToken);
            }

            return new WorkerSwitchResult(interpreter(), restarted);
        }
    }

    internal sealed class CountingDownloader(IFileDownloader inner) : IFileDownloader
    {
        private int _downloaded;
        private long _bytes;

        public int Downloaded => Volatile.Read(ref _downloaded);

        public long Bytes => Interlocked.Read(ref _bytes);

        public async Task<bool> DownloadAsync(PinnedFile file, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            bool downloaded = await inner.DownloadAsync(file, destination, progress, cancellationToken);
            if (downloaded)
            {
                Interlocked.Increment(ref _downloaded);
                Interlocked.Add(ref _bytes, file.Size);
            }

            return downloaded;
        }
    }

    private sealed class EchoLog(ISetupLog file, ITestOutputHelper output) : ISetupLog
    {
        public string? FilePath => file.FilePath;

        public void Write(string line)
        {
            file.Write(line);
            try
            {
                output.WriteLine(line);
            }
            catch (InvalidOperationException)
            {
                // the test already finished
            }
        }
    }

    private sealed class InlineProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => report(value);
    }
}
