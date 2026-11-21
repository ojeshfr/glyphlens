using System.IO.Compression;
using System.Security.Cryptography;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Core.Setup;
using GlyphLens.Infrastructure.Setup;

namespace GlyphLens.Infrastructure.Tests.Setup;

/// <summary>
/// The setup's orchestration with fakes: no network, no uv, no worker. Real files are written into a temporary folder (runtime,
/// models) so the "already in place" checks, the clean-up and "nothing is written outside the two folders" are real.
/// </summary>
public sealed class EnvironmentInstallerTests : IDisposable
{
    private static readonly byte[] FakeUvExe = RandomNumberGenerator.GetBytes(4096);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "glyphlens-installer-" + Guid.NewGuid().ToString("N"));

    public EnvironmentInstallerTests()
    {
        Directory.CreateDirectory(_root);
        Runtime = Path.Combine(_root, "runtime");
        Models = Path.Combine(_root, "models");
        Downloader = new FakeDownloader(UvZip());
        Runner = new FakeRunner(Runtime);
        Catalog = new FakeCatalog(Models);
        Worker = new FakeWorker();
        Switch = new FakeSwitch(Worker, () => RuntimeDirectory.VirtualEnvironmentPython(Runtime));
        Log = new ListLog();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Runtime { get; }

    private string Models { get; }

    private FakeDownloader Downloader { get; }

    private FakeRunner Runner { get; }

    private FakeCatalog Catalog { get; }

    private FakeWorker Worker { get; }

    private FakeSwitch Switch { get; }

    private ListLog Log { get; }

    private long? FreeBytes { get; set; } = 50L * 1024 * 1024 * 1024;

    private string? SystemProxy { get; set; }

    private Dictionary<string, string> ProcessEnvironment { get; } = [];

    private static byte[] UvZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("uv.exe", FakeUvExe), ("uvw.exe", new byte[] { 1 }), ("uvx.exe", new byte[] { 2 }) })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private EnvironmentInstaller Create(RuntimeLocation? runtime = null, string? models = "default")
    {
        byte[] zip = Downloader.UvZip;
        return new EnvironmentInstaller(new EnvironmentInstallerServices(
            runtime ?? new RuntimeLocation(Runtime, null),
            models == "default" ? Models : models,
            Downloader,
            Runner,
            Catalog,
            Worker,
            Switch,
            Log,
            EngineLock.Parse("abc==1.0 \\\n    --hash=sha256:" + new string('a', 64) + "\n"))
        {
            UvArchive = SetupManifest.Uv with { Size = zip.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)) },
            UvExecutable = (FakeUvExe.Length, Convert.ToHexStringLower(SHA256.HashData(FakeUvExe))),
            FreeBytes = _ => FreeBytes,
            ProxyFor = _ => SystemProxy is null ? null : new Uri(SystemProxy),
            GetEnvironmentVariable = name => ProcessEnvironment.GetValueOrDefault(name),
        });
    }

    private static readonly SetupOptions Both = new(IncludeTraditional: true);

    private sealed class Recorder : IProgress<SetupProgress>
    {
        public List<SetupProgress> Reports { get; } = [];

        public void Report(SetupProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }

        public List<(SetupStepId Step, SetupStepState State)> Transitions()
        {
            lock (Reports)
            {
                return [.. Reports.Where(r => r.State != SetupStepState.Running || r.BytesDone is null).Select(r => (r.Step, r.State)).Distinct()];
            }
        }
    }

    // ---- a full run ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshRun_DoesEveryStepInOrder_AndEndsChecked()
    {
        var progress = new Recorder();

        var result = await Create().RunAsync(Both, progress, CancellationToken.None);

        Assert.Empty(result.Skipped);
        Assert.Empty(result.Warnings);
        SetupStepId[] order = [SetupStepId.Prepare, SetupStepId.Uv, SetupStepId.Python, SetupStepId.Packages, SetupStepId.OcrModels, SetupStepId.Worker, SetupStepId.SimplifiedModel, SetupStepId.TraditionalModel, SetupStepId.Check];
        Assert.Equal(order.SelectMany(s => new[] { (s, SetupStepState.Running), (s, SetupStepState.Done) }), progress.Transitions());
        Assert.True(File.Exists(RuntimeDirectory.UvExecutable(Runtime)));
        Assert.Equal(FakeUvExe, File.ReadAllBytes(RuntimeDirectory.UvExecutable(Runtime)));
        Assert.True(File.Exists(RuntimeDirectory.LockMarker(Runtime)));
        Assert.Equal(["zh", "zt"], Catalog.Imported.Select(i => i.Source));
        Assert.All(Catalog.Imported, i => Assert.StartsWith(RuntimeDirectory.DownloadsFolder(Runtime), i.Path, StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(RuntimeDirectory.DownloadsFolder(Runtime))); // archives deleted after import
        Assert.All(SetupManifest.OcrFiles, f => Assert.True(File.Exists(Path.Combine(Models, f.RelativePath))));
        Assert.Equal(1, Switch.Calls);
        Assert.True(Switch.LastRestart); // the environment changed under the worker
    }

    [Fact]
    public async Task TheUvCommands_KeepEverythingInsideTheRuntimeFolder_AndInstallOnlyTheHashLockedPackages()
    {
        await Create().RunAsync(Both, null, CancellationToken.None);

        var uv = Runner.Requests.Where(r => r.FileName == RuntimeDirectory.UvExecutable(Runtime)).ToList();
        var install = uv.Single(r => r.Arguments[0] == "python");
        Assert.Equal(["python", "install", SetupManifest.PythonRequest, "--install-dir", RuntimeDirectory.PythonInstallFolder(Runtime), "--no-bin", "--no-registry"], install.Arguments.Take(7));
        var venv = uv.Single(r => r.Arguments[0] == "venv");
        Assert.Equal(RuntimeDirectory.VirtualEnvironmentFolder(Runtime), venv.Arguments[1]);
        Assert.Contains("--managed-python", venv.Arguments);
        Assert.Contains("--no-python-downloads", venv.Arguments);
        var pip = uv.Single(r => r.Arguments[0] == "pip");
        foreach (string flag in new[] { "--require-hashes", "--no-deps", "--no-build", "--strict" })
        {
            Assert.Contains(flag, pip.Arguments);
        }

        Assert.Equal(RuntimeDirectory.VirtualEnvironmentPython(Runtime), pip.Arguments[pip.Arguments.ToList().IndexOf("--python") + 1]);
        Assert.Equal(RuntimeDirectory.LockFile(Runtime), pip.Arguments[pip.Arguments.ToList().IndexOf("-r") + 1]);
        Assert.Equal("https://pypi.org/simple", pip.Arguments[pip.Arguments.ToList().IndexOf("--default-index") + 1]);
        Assert.Equal(EngineLock.Parse("abc==1.0 \\\n    --hash=sha256:" + new string('a', 64) + "\n").Text, File.ReadAllText(RuntimeDirectory.LockFile(Runtime)));

        foreach (var request in uv)
        {
            Assert.Contains("--no-config", request.Arguments);
            Assert.Contains("--no-progress", request.Arguments);
            Assert.Equal(RuntimeDirectory.PythonInstallFolder(Runtime), request.Environment["UV_PYTHON_INSTALL_DIR"]);
            Assert.Equal(RuntimeDirectory.CacheFolder(Runtime), request.Environment["UV_CACHE_DIR"]);
            Assert.Equal("1", request.Environment["UV_NO_CONFIG"]);
            Assert.Equal("1", request.Environment["UV_MANAGED_PYTHON"]);
            Assert.Equal("automatic", request.Environment["UV_PYTHON_DOWNLOADS"]);
            Assert.Equal("copy", request.Environment["UV_LINK_MODE"]);
            Assert.Equal("1", request.Environment["UV_NO_PROGRESS"]);
            Assert.StartsWith(Runtime, request.Environment["UV_PYTHON_BIN_DIR"], StringComparison.Ordinal);
            Assert.False(request.Environment.ContainsKey("HTTPS_PROXY"));
        }

        foreach (var request in Runner.Requests)
        {
            Assert.Contains("PYTHON", request.RemoveEnvironmentPrefixes);
            Assert.Contains("UV_", request.RemoveEnvironmentPrefixes);
            Assert.Contains("PIP_", request.RemoveEnvironmentPrefixes);
            Assert.Equal(Runtime, request.WorkingDirectory);
            Assert.True(request.Timeout > TimeSpan.Zero);
        }

        // Every Python check runs isolated (-I), so nothing from the user's environment or the current folder is imported.
        Assert.All(Runner.Requests.Where(r => r.FileName == RuntimeDirectory.VirtualEnvironmentPython(Runtime)), r => Assert.Equal("-I", r.Arguments[0]));
    }

    [Fact]
    public async Task NothingIsWrittenOutsideTheRuntimeAndModelsFolders()
    {
        await Create().RunAsync(Both, null, CancellationToken.None);

        var top = Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(["models", "runtime"], top);
    }

    [Fact]
    public async Task ASecondRun_SkipsEverything_AndDownloadsNothing()
    {
        await Create().RunAsync(Both, null, CancellationToken.None);
        int downloads = Downloader.Downloaded.Count;
        int uvCalls = Runner.UvCalls;
        Worker.Start(); // as in the app: the worker now runs on the new environment

        var result = await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.Equal([SetupStepId.Uv, SetupStepId.Python, SetupStepId.Packages, SetupStepId.OcrModels, SetupStepId.Worker, SetupStepId.SimplifiedModel, SetupStepId.TraditionalModel], result.Skipped);
        Assert.Equal(downloads, Downloader.Downloaded.Count);
        Assert.Equal(uvCalls, Runner.UvCalls);
        Assert.Equal(2, Catalog.Imported.Count);
        Assert.False(Switch.LastRestart);
    }

    [Fact]
    public async Task WithoutTraditional_NoTraditionalModelIsDownloaded()
    {
        var progress = new Recorder();

        await Create().RunAsync(new SetupOptions(IncludeTraditional: false), progress, CancellationToken.None);

        Assert.DoesNotContain(progress.Reports, r => r.Step == SetupStepId.TraditionalModel);
        Assert.DoesNotContain(Downloader.Downloaded, d => d == SetupManifest.TraditionalChineseModel.Id);
        Assert.Equal(["zh"], Catalog.Imported.Select(i => i.Source));
    }

    // ---- failures and resuming -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailingPackageInstall_NamesTheStep_DoesNotMarkTheEnvironmentDone_AndTryAgainResumes()
    {
        Runner.PipExitCode = 1;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.Packages, ex.Step);
        Assert.Contains("could not be downloaded or installed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 1", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(RuntimeDirectory.LockMarker(Runtime)));
        Assert.Empty(Catalog.Imported);

        Runner.PipExitCode = 0;
        int uvDownloads = Downloader.Downloaded.Count(d => d == "uv");
        var result = await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.Equal([SetupStepId.Uv, SetupStepId.Python], result.Skipped);
        Assert.Equal(uvDownloads, Downloader.Downloaded.Count(d => d == "uv"));
        Assert.Equal(1, Runner.Requests.Count(r => r.Arguments.Count > 0 && r.Arguments[0] == "venv")); // not created again
        Assert.True(File.Exists(RuntimeDirectory.LockMarker(Runtime)));
    }

    [Fact]
    public async Task PackagesThatInstallButDoNotLoad_AreAFailure()
    {
        Runner.ImportExitCode = 1;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.Packages, ex.Step);
        Assert.Contains("do not load", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(RuntimeDirectory.LockMarker(Runtime)));
    }

    [Fact]
    public async Task ADownloadThatDoesNotVerify_FailsTheStepWithTheServerChangedMessage()
    {
        Downloader.FailFor = SetupManifest.OcrFiles[2].File.Id;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.OcrModels, ex.Step);
        Assert.Contains("The file on the server changed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("do not use it", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Catalog.Imported);
    }

    [Fact]
    public async Task AnEnvironmentBasedOnAnotherPython_IsNotAccepted_AndIsCreatedAgainWithItsPackages()
    {
        await Create().RunAsync(Both, null, CancellationToken.None);
        Runner.BasePrefixOverride = @"C:\Python314"; // e.g. a venv that points at the system Python

        var result = await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.DoesNotContain(SetupStepId.Python, result.Skipped);
        Assert.DoesNotContain(SetupStepId.Packages, result.Skipped);
        Assert.Equal(2, Runner.Requests.Count(r => r.Arguments.Count > 0 && r.Arguments[0] == "venv"));
        Assert.Equal(2, Runner.Requests.Count(r => r.Arguments.Count > 0 && r.Arguments[0] == "pip"));
    }

    [Fact]
    public async Task AnEnvironmentThatStillDoesNotWorkAfterCreation_IsAFailureOfThePythonStep()
    {
        Runner.BasePrefixOverride = @"C:\Python314";
        Runner.KeepBasePrefix = true;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.Python, ex.Step);
        Assert.Contains("does not work", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooLittleDiskSpace_FailsBeforeAnythingIsDownloaded()
    {
        FreeBytes = 200L * 1024 * 1024;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.Prepare, ex.Step);
        Assert.Contains("Not enough free disk space", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Downloader.Downloaded);
        Assert.Empty(Runner.Requests);
    }

    [Fact]
    public async Task AnInvalidRuntimeFolder_IsReportedByThePlan_AndTheRunRefuses()
    {
        var installer = Create(new RuntimeLocation(null, "GLYPHLENS_RUNTIME_DIR is not an absolute local folder, so the setup will not use it."));

        var ex = Assert.Throws<SetupException>(() => installer.Plan(Both));

        Assert.Contains("GLYPHLENS_RUNTIME_DIR", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SetupException>(() => installer.RunAsync(Both, null, CancellationToken.None));
        Assert.Empty(Downloader.Downloaded);
        Assert.Empty(Runner.Requests);
    }

    [Fact]
    public void NoModelsFolder_IsReportedByThePlan() =>
        Assert.Throws<SetupException>(() => Create(models: null).Plan(Both));

    [Fact]
    public async Task Cancelling_Propagates_AndTheEnvironmentIsNotMarkedComplete()
    {
        using var cts = new CancellationTokenSource();
        Runner.OnPip = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().RunAsync(Both, null, cts.Token));

        Assert.False(File.Exists(RuntimeDirectory.LockMarker(Runtime)));
        Assert.Empty(Catalog.Imported);
        Assert.Contains(Log.Lines, l => l.Contains("cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StalePartialFiles_FromAnInterruptedRun_AreRemoved()
    {
        string downloads = RuntimeDirectory.DownloadsFolder(Runtime);
        Directory.CreateDirectory(downloads);
        string ocr = Path.GetDirectoryName(Path.Combine(Models, SetupManifest.OcrFiles[0].RelativePath))!;
        Directory.CreateDirectory(ocr);
        File.WriteAllText(Path.Combine(downloads, "translate-zh_en-1_9.argosmodel.part"), "x");
        File.WriteAllText(Path.Combine(ocr, "inference.onnx.part"), "x");

        await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AConfiguredOtherPython_IsReportedAsAWarning()
    {
        Switch.Interpreter = () => @"C:\Python312\python.exe";

        var result = await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("GLYPHLENS_PYTHON", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WorkerComponentState.Unavailable, WorkerComponentState.Installed, "OCR packages")]
    [InlineData(WorkerComponentState.NotInstalled, WorkerComponentState.Installed, "OCR models")]
    [InlineData(WorkerComponentState.Installed, WorkerComponentState.Unavailable, "translation")]
    public async Task TheFinalCheck_FailsWhenTheWorkerStillReportsSomethingMissing(WorkerComponentState ocr, WorkerComponentState translation, string expected)
    {
        Worker.Ocr = ocr;
        Worker.Translation = translation;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.Check, ex.Step);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFinalCheck_FailsWhenTheWorkerReadsAnotherModelsFolder()
    {
        Catalog.StoragePath = Path.Combine(_root, "elsewhere");

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Contains("another folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImportedPackageOfTheWrongLanguage_IsAFailure()
    {
        Catalog.WrongPair = true;

        var ex = await Assert.ThrowsAsync<SetupException>(() => Create().RunAsync(Both, null, CancellationToken.None));

        Assert.Equal(SetupStepId.SimplifiedModel, ex.Step);
    }

    [Fact]
    public async Task AnAlreadyInstalledModel_IsNotDownloaded()
    {
        Catalog.Add("zh");

        await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.DoesNotContain(SetupManifest.SimplifiedChineseModel.Id, Downloader.Downloaded);
        Assert.Contains(SetupManifest.TraditionalChineseModel.Id, Downloader.Downloaded);
    }

    // ---- proxy, plan, concurrency ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSystemProxy_IsHandedToUv_UnlessTheUserSetOne()
    {
        SystemProxy = "http://proxy.example:8080/";
        await Create().RunAsync(Both, null, CancellationToken.None);
        Assert.All(Runner.Requests.Where(r => r.FileName.EndsWith("uv.exe", StringComparison.Ordinal)), r => Assert.Equal("http://proxy.example:8080/", r.Environment["HTTPS_PROXY"]));

        Runner.Requests.Clear();
        ProcessEnvironment["HTTPS_PROXY"] = "http://mine.example:3128";
        File.Delete(RuntimeDirectory.LockMarker(Runtime));
        await Create().RunAsync(Both, null, CancellationToken.None);
        Assert.All(Runner.Requests.Where(r => r.FileName.EndsWith("uv.exe", StringComparison.Ordinal)), r => Assert.False(r.Environment.ContainsKey("HTTPS_PROXY")));
    }

    [Fact]
    public void ThePlan_ListsTheFilesTheFoldersAndTheLog()
    {
        var both = Create().Plan(Both);
        var simplified = Create().Plan(new SetupOptions(false));

        Assert.Equal(Runtime, both.RuntimeFolder);
        Assert.Equal(Models, both.ModelsFolder);
        Assert.Equal(RuntimeDirectory.LogFile(Runtime), both.LogFile);
        Assert.Equal(1, both.PackageCount);
        Assert.Contains(SetupManifest.TraditionalChineseModel, both.Files);
        Assert.DoesNotContain(SetupManifest.TraditionalChineseModel, simplified.Files);
        Assert.DoesNotContain(SetupStepId.TraditionalModel, simplified.Steps);
        Assert.Equal(SetupManifest.TraditionalChineseModel.Size, both.ApproximateDownloadBytes - simplified.ApproximateDownloadBytes);
        Assert.Equal(SetupStepId.Prepare, both.Steps[0]);
        Assert.Equal(SetupStepId.Check, both.Steps[^1]);
    }

    [Fact]
    public async Task ASecondRunWhileOneIsRunning_IsRefused()
    {
        var gate = new TaskCompletionSource();
        Runner.PipGate = gate.Task;
        var installer = Create();

        var first = installer.RunAsync(Both, null, CancellationToken.None);
        await Runner.PipStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<SetupException>(() => installer.RunAsync(Both, null, CancellationToken.None));
        gate.SetResult();
        await first;

        Assert.Contains("already running", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLog_HasTheCommandsAndOutcome_ButNoPayloads()
    {
        await Create().RunAsync(Both, null, CancellationToken.None);

        Assert.Contains(Log.Lines, l => l.StartsWith("=== Setup started", StringComparison.Ordinal));
        Assert.Contains(Log.Lines, l => l.StartsWith("> uv.exe pip install", StringComparison.Ordinal));
        Assert.Contains(Log.Lines, l => l.StartsWith("=== Setup finished", StringComparison.Ordinal));
    }

    // ---- fakes ----------------------------------------------------------------------------------------------------------

    private sealed class FakeDownloader(byte[] uvZip) : IFileDownloader
    {
        public byte[] UvZip { get; } = uvZip;

        public List<string> Downloaded { get; } = [];

        public string? FailFor { get; set; }

        public Task<bool> DownloadAsync(PinnedFile file, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Id == FailFor)
            {
                throw new DownloadException(DownloadFailureKind.HashMismatch, HttpFileDownloader.HashMessage(file));
            }

            if (File.Exists(destination))
            {
                return Task.FromResult(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.Id == "uv" ? UvZip : [1, 2, 3]);
            progress?.Report(file.Size / 2);
            progress?.Report(file.Size);
            Downloaded.Add(file.Id);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRunner(string runtime) : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];

        public int PipExitCode { get; set; }

        public int ImportExitCode { get; set; }

        public string? BasePrefixOverride { get; set; }

        public bool KeepBasePrefix { get; set; }

        public Action? OnPip { get; set; }

        public Task? PipGate { get; set; }

        public TaskCompletionSource PipStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int UvCalls => Requests.Count(r => r.FileName == RuntimeDirectory.UvExecutable(runtime));

        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, Action<string>? outputLine, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request);
            }

            string venvPython = RuntimeDirectory.VirtualEnvironmentPython(runtime);
            if (request.FileName == venvPython)
            {
                Assert.True(File.Exists(venvPython));
                string script = request.Arguments[^1];
                if (script.Contains("base_prefix", StringComparison.Ordinal))
                {
                    outputLine?.Invoke("3.12");
                    outputLine?.Invoke(BasePrefixOverride ?? Path.Combine(RuntimeDirectory.PythonInstallFolder(runtime), SetupManifest.PythonRequest));
                    return new ProcessRunResult(0, false);
                }

                if (script.Contains("find_spec", StringComparison.Ordinal))
                {
                    return new ProcessRunResult(0, false);
                }

                return new ProcessRunResult(ImportExitCode, false);
            }

            Assert.Equal(RuntimeDirectory.UvExecutable(runtime), request.FileName);
            Assert.True(File.Exists(request.FileName));
            switch (request.Arguments[0])
            {
                case "venv":
                    string scripts = Path.GetDirectoryName(venvPython)!;
                    if (Directory.Exists(request.Arguments[1]))
                    {
                        Directory.Delete(request.Arguments[1], recursive: true); // --clear
                    }

                    Directory.CreateDirectory(scripts);
                    File.WriteAllText(venvPython, "python");
                    if (!KeepBasePrefix)
                    {
                        BasePrefixOverride = null;
                    }

                    return new ProcessRunResult(0, false);
                case "python":
                    Directory.CreateDirectory(Path.Combine(RuntimeDirectory.PythonInstallFolder(runtime), SetupManifest.PythonRequest));
                    return new ProcessRunResult(0, false);
                case "pip":
                    PipStarted.TrySetResult();
                    OnPip?.Invoke();
                    if (PipGate is not null)
                    {
                        await PipGate;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return new ProcessRunResult(PipExitCode, false);
                default:
                    return new ProcessRunResult(0, false);
            }
        }
    }

    private sealed class FakeCatalog(string models) : IModelCatalog
    {
        private readonly List<TranslationPackageInfo> _packages = [];

        public event EventHandler? Changed;

        public string StoragePath { get; set; } = models;

        public bool WrongPair { get; set; }

        public List<(string Source, string Path)> Imported { get; } = [];

        public void Add(string source) => _packages.Add(new TranslationPackageInfo(source, "en", source, "English", "1.9", $"translate-{source}_en"));

        public Task<ModelCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCatalog("argos", StoragePath, [.. _packages], []));

        public Task<RouteStatus> GetRouteStatusAsync(string source, string target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TranslationPackageInfo> ImportPackageAsync(string localFilePath, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(localFilePath), "the archive must exist while it is imported");
            string source = Path.GetFileName(localFilePath).Contains("zt_en", StringComparison.Ordinal) ? "zt" : "zh";
            Imported.Add((source, localFilePath));
            if (WrongPair)
            {
                source = "fr";
            }

            Add(source);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(_packages[^1]);
        }
    }

    private sealed class FakeWorker : IWorkerProcessManager
    {
        public WorkerComponentState Ocr { get; set; } = WorkerComponentState.Installed;

        public WorkerComponentState Translation { get; set; } = WorkerComponentState.Installed;

        public WorkerStatus Status { get; private set; } = WorkerStatus.Stopped;

        public int? ProcessId => null;

        public string? LastError => null;

        public WorkerHealth? LastHealth { get; private set; }

        public event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;

        public void Start() => Status = WorkerStatus.Ready;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Start();
            StatusChanged?.Invoke(this, new WorkerStatusChangedEventArgs(WorkerStatus.Stopped, WorkerStatus.Ready));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = WorkerStatus.Stopped;
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

        public Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
        {
            LastHealth = new WorkerHealth(
                WorkerComponentState.Ready, WorkerComponentState.Connected, WorkerComponentState.Initialized, Ocr, WorkerComponentState.Ready, Translation,
                WorkerComponentState.Installed, "0.1.0", ProtocolVersion.Current, "3.12.16", 1234);
            return Task.FromResult(LastHealth);
        }
    }

    private sealed class FakeSwitch(FakeWorker worker, Func<string> defaultInterpreter) : IWorkerEnvironmentSwitch
    {
        public Func<string> Interpreter { get; set; } = defaultInterpreter;

        public int Calls { get; private set; }

        public bool LastRestart { get; private set; }

        public async Task<WorkerSwitchResult> UseInterpreterAsync(string pythonPath, bool restart, CancellationToken cancellationToken)
        {
            Calls++;
            LastRestart = restart;
            bool restarted = restart || worker.Status != WorkerStatus.Ready;
            if (restarted)
            {
                await worker.RestartAsync(cancellationToken);
            }

            return new WorkerSwitchResult(Interpreter(), restarted);
        }
    }

    private sealed class ListLog : ISetupLog
    {
        public List<string> Lines { get; } = [];

        public string? FilePath => null;

        public void Write(string line)
        {
            lock (Lines)
            {
                Lines.Add(line);
            }
        }
    }
}
