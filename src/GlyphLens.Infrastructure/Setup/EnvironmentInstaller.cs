using System.Globalization;
using System.Net;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;
using GlyphLens.Core.Setup;

namespace GlyphLens.Infrastructure.Setup;

/// <summary>Where the setup installs, and the services it uses.</summary>
/// <param name="Runtime">The runtime folder (see <see cref="RuntimeDirectory"/>).</param>
/// <param name="ModelsDirectory">The models folder the worker reads, or <see langword="null"/> when none can be determined.</param>
/// <param name="Downloader">Downloads pinned files.</param>
/// <param name="Processes">Runs uv and the Python checks.</param>
/// <param name="Catalog">Imports translation packages through the worker's validated importer and lists what is installed.</param>
/// <param name="Worker">The worker, for its health report.</param>
/// <param name="WorkerSwitch">Moves the worker onto the installed interpreter.</param>
/// <param name="Log">The technical log.</param>
/// <param name="Lock">The hash-locked engine packages.</param>
public sealed record EnvironmentInstallerServices(
    RuntimeLocation Runtime,
    string? ModelsDirectory,
    IFileDownloader Downloader,
    IProcessRunner Processes,
    IModelCatalog Catalog,
    IWorkerProcessManager Worker,
    IWorkerEnvironmentSwitch WorkerSwitch,
    ISetupLog Log,
    EngineLock Lock)
{
    /// <summary>Free bytes on the drive holding a path (tests inject a value; <see langword="null"/> = unknown).</summary>
    public Func<string, long?> FreeBytes { get; init; } = DefaultFreeBytes;

    /// <summary>The proxy to hand to uv for an address, or <see langword="null"/> for none (default: the system proxy).</summary>
    public Func<Uri, Uri?> ProxyFor { get; init; } = DefaultProxyFor;

    /// <summary>The uv release archive (tests use a small stand-in; the default is the pinned release).</summary>
    public PinnedFile UvArchive { get; init; } = SetupManifest.Uv;

    /// <summary>The size and SHA-256 of <c>uv.exe</c> inside <see cref="UvArchive"/>.</summary>
    public (long Size, string Sha256) UvExecutable { get; init; } = (SetupManifest.UvExecutableSize, SetupManifest.UvExecutableSha256);

    /// <summary>Environment variables of this process (injected for tests).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    private static long? DefaultFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Uri? DefaultProxyFor(Uri destination)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(destination);
            return proxy is null || proxy == destination || HttpClient.DefaultProxy.IsBypassed(destination) ? null : proxy;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or WebException)
        {
            return null;
        }
    }
}

/// <summary>
/// The one-click setup: installs uv, a uv-managed Python 3.12 with a virtual environment, the hash-locked engine packages, the PP-OCRv5
/// model files and the Chinese translation models, then restarts the worker on the new environment and checks that it reports OCR and
/// translation as available. Runs only when the user starts it.
/// </summary>
/// <remarks>
/// <para>
/// Every step first checks whether its result is already in place and verifies (file hashes, the interpreter's version and origin, a
/// marker holding the lock file's hash, the worker's catalogue); if so it is skipped, so a second run downloads nothing and a failed or
/// cancelled run resumes where it stopped. Everything is written below the runtime folder, except the model files (the models folder).
/// No administrator rights are needed and nothing else on the system is changed: uv is told not to register Python in the Windows
/// registry and not to put executables on the PATH, its cache and Python folder are inside the runtime folder, and every inherited
/// <c>PYTHON*</c>, <c>UV_*</c>, <c>PIP_*</c> and <c>CONDA*</c> variable is removed for the child processes.
/// </para>
/// <para>The log receives commands and installer output only; the setup never handles screen content.</para>
/// </remarks>
public sealed class EnvironmentInstaller : IEnvironmentSetup
{
    private static readonly string[] RemovedPrefixes = ["PYTHON", "UV_", "PIP_", "VIRTUAL_ENV", "CONDA"];
    private static readonly string[] RequiredModules = ["ctranslate2", "sentencepiece", "onnxruntime", "paddleocr", "paddlex", "cv2", "numpy"];

    private readonly EnvironmentInstallerServices _s;
    private int _running;

    public EnvironmentInstaller(EnvironmentInstallerServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _s = services;
    }

    public SetupPlan Plan(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (runtime, models) = Locations();
        var steps = new List<SetupStepId>
        {
            SetupStepId.Prepare, SetupStepId.Uv, SetupStepId.Python, SetupStepId.Packages, SetupStepId.OcrModels, SetupStepId.Worker, SetupStepId.SimplifiedModel,
        };
        var files = new List<PinnedFile> { _s.UvArchive };
        files.AddRange(SetupManifest.OcrFiles.Select(f => f.File));
        files.Add(SetupManifest.SimplifiedChineseModel);
        if (options.IncludeTraditional)
        {
            steps.Add(SetupStepId.TraditionalModel);
            files.Add(SetupManifest.TraditionalChineseModel);
        }

        steps.Add(SetupStepId.Check);
        long bytes = files.Sum(f => f.Size) + SetupManifest.ApproximatePythonDownloadBytes + SetupManifest.ApproximatePackageDownloadBytes;
        return new SetupPlan(steps, files, bytes, runtime, models, RuntimeDirectory.LogFile(runtime), _s.Lock.Packages.Count);
    }

    public async Task<SetupResult> RunAsync(SetupOptions options, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new SetupException(SetupStepId.Prepare, "The setup is already running.");
        }

        try
        {
            var plan = Plan(options);
            var run = new Run(this, plan, options, progress, cancellationToken);
            Log($"=== Setup started (uv {SetupManifest.UvVersion}, Python {SetupManifest.PythonRequest}, lock {_s.Lock.Sha256[..12]}, Traditional: {options.IncludeTraditional}).");
            Log($"Runtime folder: {plan.RuntimeFolder}. Models folder: {plan.ModelsFolder}.");
            try
            {
                var result = await run.ExecuteAsync().ConfigureAwait(false);
                Log("=== Setup finished." + (result.Warnings.Count > 0 ? " Warnings: " + string.Join(" ", result.Warnings) : string.Empty));
                return result;
            }
            catch (OperationCanceledException)
            {
                Log("=== Setup cancelled by the user.");
                throw;
            }
            catch (SetupException ex)
            {
                Log($"=== Setup failed at {ex.Step}: {ex.Message}" + (ex.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : string.Empty));
                throw;
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _running, 0);
        }
    }

    private (string Runtime, string Models) Locations()
    {
        if (_s.Runtime.Root is not { } runtime)
        {
            throw new SetupException(SetupStepId.Prepare, _s.Runtime.Problem ?? "No runtime folder could be determined.");
        }

        if (_s.ModelsDirectory is not { } models)
        {
            throw new SetupException(SetupStepId.Prepare, "No models folder could be determined (LOCALAPPDATA is not set).");
        }

        return (runtime, models);
    }

    private void Log(string line) => _s.Log.Write(line);

    /// <summary>One run's state.</summary>
    private sealed class Run(EnvironmentInstaller owner, SetupPlan plan, SetupOptions options, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        private readonly EnvironmentInstallerServices _s = owner._s;
        private readonly string _runtime = plan.RuntimeFolder;
        private readonly string _models = plan.ModelsFolder;
        private readonly List<SetupStepId> _skipped = [];
        private readonly List<string> _warnings = [];
        private bool _environmentChanged;

        private string VenvPython => RuntimeDirectory.VirtualEnvironmentPython(_runtime);

        private string Uv => RuntimeDirectory.UvExecutable(_runtime);

        public async Task<SetupResult> ExecuteAsync()
        {
            await StepAsync(SetupStepId.Prepare, "Checking the folders and the free disk space…", PrepareAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.Uv, "Getting uv, the tool that installs Python…", InstallUvAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.Python, "Installing Python 3.12 (managed by uv, only for GlyphLens)…", PythonAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.Packages, "Installing the OCR and translation packages…", PackagesAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.OcrModels, "Downloading the Chinese OCR models…", OcrModelsAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.Worker, "Restarting the worker with the new environment…", WorkerAsync).ConfigureAwait(false);
            await StepAsync(SetupStepId.SimplifiedModel, "Installing the Simplified Chinese model…", () => TranslationModelAsync(SetupStepId.SimplifiedModel, SetupManifest.SimplifiedChineseModel, "zh")).ConfigureAwait(false);
            if (options.IncludeTraditional)
            {
                await StepAsync(SetupStepId.TraditionalModel, "Installing the Traditional Chinese model…", () => TranslationModelAsync(SetupStepId.TraditionalModel, SetupManifest.TraditionalChineseModel, "zt")).ConfigureAwait(false);
            }

            await StepAsync(SetupStepId.Check, "Checking that OCR and translation work…", CheckAsync).ConfigureAwait(false);
            return new SetupResult([.. _skipped], [.. _warnings]);
        }

        /// <summary>Runs one step: reports it, maps unexpected failures to a plain message for that step, records a skip.</summary>
        private async Task StepAsync(SetupStepId step, string message, Func<Task<bool>> body)
        {
            ct.ThrowIfCancellationRequested();
            Report(step, SetupStepState.Running, message);
            owner.Log($"--- {step}: {message}");
            bool didWork;
            try
            {
                didWork = await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SetupException)
            {
                throw;
            }
            catch (DownloadException ex)
            {
                throw new SetupException(step, ex.Message, ex);
            }
            catch (WorkerException ex)
            {
                throw new SetupException(step, "The worker could not be used: " + ex.Message, ex);
            }
            catch (TranslationException ex)
            {
                throw new SetupException(step, "The model could not be installed: " + ex.Error.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                throw new SetupException(step, Failure(step) + $" ({ex.GetType().Name})", ex);
            }

            if (!didWork)
            {
                _skipped.Add(step);
            }

            owner.Log($"--- {step}: {(didWork ? "done" : "already in place, skipped")}.");
            Report(step, didWork ? SetupStepState.Done : SetupStepState.Skipped, didWork ? "Done." : "Already in place.");
        }

        private static string Failure(SetupStepId step) => step switch
        {
            SetupStepId.Prepare => "The setup folders could not be prepared.",
            SetupStepId.Uv => "uv could not be installed.",
            SetupStepId.Python => "Python could not be installed.",
            SetupStepId.Packages => "The OCR and translation packages could not be installed.",
            SetupStepId.OcrModels => "The OCR models could not be installed.",
            SetupStepId.Worker => "The worker could not be restarted.",
            SetupStepId.SimplifiedModel or SetupStepId.TraditionalModel => "The translation model could not be installed.",
            _ => "The final check failed.",
        };

        private void Report(SetupStepId step, SetupStepState state, string message, long? done = null, long? total = null) =>
            progress?.Report(new SetupProgress(step, state, message, done, total));

        // ---- steps (each returns whether it did any work) ---------------------------------------------------------------

        private Task<bool> PrepareAsync()
        {
            Directory.CreateDirectory(_runtime);
            Directory.CreateDirectory(RuntimeDirectory.DownloadsFolder(_runtime));

            // Partial files of an earlier run that was cut off (a crash or power loss: a normal failure removes its own).
            foreach (string folder in new[] { RuntimeDirectory.DownloadsFolder(_runtime), RuntimeDirectory.ToolsFolder(_runtime) }
                .Concat(SetupManifest.OcrFiles.Select(f => Path.GetDirectoryName(Path.Combine(_models, f.RelativePath))!)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (string stale in Directory.EnumerateFiles(folder, "*" + HttpFileDownloader.PartialSuffix))
                {
                    File.Delete(stale);
                    owner.Log($"Removed a partial file from an earlier run: {Path.GetFileName(stale)}");
                }
            }

            bool environmentReady = File.Exists(RuntimeDirectory.LockMarker(_runtime));
            long needed = environmentReady ? SetupManifest.RequiredFreeBytesForModels : SetupManifest.RequiredFreeBytes;
            RequireSpace(_runtime, needed);
            if (!SameDrive(_runtime, _models))
            {
                RequireSpace(_models, SetupManifest.RequiredFreeBytesForModels);
            }

            return Task.FromResult(true);
        }

        private void RequireSpace(string path, long needed)
        {
            long? free = _s.FreeBytes(path);
            owner.Log(string.Create(CultureInfo.InvariantCulture, $"Free space for {path}: {(free is { } f ? $"{f / (1024 * 1024)} MB" : "unknown")}; needed about {needed / (1024 * 1024)} MB."));
            if (free is { } available && available < needed)
            {
                throw new SetupException(
                    SetupStepId.Prepare,
                    string.Create(CultureInfo.InvariantCulture, $"Not enough free disk space on {Path.GetPathRoot(path)}: about {needed / (1024 * 1024)} MB are needed and {available / (1024 * 1024)} MB are free. Free some space and try again."));
            }
        }

        private static bool SameDrive(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        private async Task<bool> InstallUvAsync()
        {
            var expected = new PinnedFile("uv.exe", "uv.exe", _s.UvArchive.Url, _s.UvExecutable.Size, _s.UvExecutable.Sha256, _s.UvArchive.Licence);
            if (await HttpFileDownloader.VerifiesAsync(Uv, expected, ct).ConfigureAwait(false))
            {
                return false;
            }

            string archive = Path.Combine(RuntimeDirectory.DownloadsFolder(_runtime), $"uv-{SetupManifest.UvVersion}-x86_64-pc-windows-msvc.zip");
            await DownloadAsync(SetupStepId.Uv, _s.UvArchive, archive, 0, _s.UvArchive.Size).ConfigureAwait(false);
            try
            {
                Setup.UvArchive.ExtractVerified(archive, SetupManifest.UvExecutableName, Uv, _s.UvExecutable.Size, _s.UvExecutable.Sha256);
            }
            catch (InvalidDataException ex)
            {
                throw new SetupException(SetupStepId.Uv, ex.Message + " The download was deleted.", ex);
            }
            finally
            {
                File.Delete(archive);
            }

            owner.Log($"uv.exe verified (SHA-256 {_s.UvExecutable.Sha256}).");
            _environmentChanged = true;
            return true;
        }

        private async Task<bool> PythonAsync()
        {
            if (await PythonIsUsableAsync().ConfigureAwait(false))
            {
                return false;
            }

            // A cut-off earlier run may have left an environment that does not work; the packages must then be installed again too.
            TryDeleteFile(RuntimeDirectory.LockMarker(_runtime));
            await RunUvAsync(SetupStepId.Python, "Python could not be downloaded or installed.", TimeSpan.FromMinutes(15),
                "python", "install", SetupManifest.PythonRequest, "--install-dir", RuntimeDirectory.PythonInstallFolder(_runtime), "--no-bin", "--no-registry").ConfigureAwait(false);
            await RunUvAsync(SetupStepId.Python, "The Python environment could not be created.", TimeSpan.FromMinutes(5),
                "venv", RuntimeDirectory.VirtualEnvironmentFolder(_runtime), "--python", SetupManifest.PythonRequest, "--managed-python", "--no-python-downloads",
                "--no-project", "--clear", "--link-mode", "copy").ConfigureAwait(false);

            if (!await PythonIsUsableAsync().ConfigureAwait(false))
            {
                throw new SetupException(SetupStepId.Python, "Python was installed but the new environment does not work. See the log for details.");
            }

            _environmentChanged = true;
            return true;
        }

        /// <summary>The venv's interpreter runs, is Python 3.12 and is based on the Python inside the runtime folder (not a system Python).</summary>
        private async Task<bool> PythonIsUsableAsync()
        {
            if (!File.Exists(VenvPython))
            {
                return false;
            }

            var lines = new List<string>();
            var result = await RunAsync(VenvPython, ["-I", "-c", "import sys; print('%d.%d' % sys.version_info[:2]); print(sys.base_prefix)"], TimeSpan.FromMinutes(1), lines.Add).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || lines.Count < 2)
            {
                return false;
            }

            string expectedVersion = string.Create(CultureInfo.InvariantCulture, $"{SetupManifest.PythonVersion.Major}.{SetupManifest.PythonVersion.Minor}");
            string pythonFolder = Path.GetFullPath(RuntimeDirectory.PythonInstallFolder(_runtime)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            bool managed = Path.GetFullPath(lines[1].Trim()).StartsWith(pythonFolder, StringComparison.OrdinalIgnoreCase);
            owner.Log($"Environment Python {lines[0].Trim()}, based on {(managed ? "the runtime folder's Python" : "another Python")}.");
            return lines[0].Trim() == expectedVersion && managed;
        }

        private async Task<bool> PackagesAsync()
        {
            string marker = RuntimeDirectory.LockMarker(_runtime);
            if (File.Exists(marker) && (await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)).Trim() == _s.Lock.Sha256
                && await ModulesPresentAsync().ConfigureAwait(false))
            {
                return false;
            }

            TryDeleteFile(marker);
            string lockFile = RuntimeDirectory.LockFile(_runtime);
            await File.WriteAllTextAsync(lockFile, _s.Lock.Text, ct).ConfigureAwait(false);
            owner.Log(string.Create(CultureInfo.InvariantCulture, $"Installing {_s.Lock.Packages.Count} locked packages (lock SHA-256 {_s.Lock.Sha256})."));

            // --reinstall: a cut-off earlier run may have left half-written packages that would otherwise count as installed.
            await RunUvAsync(SetupStepId.Packages, "The OCR and translation packages could not be downloaded or installed. Check the internet connection.", TimeSpan.FromMinutes(40),
                "pip", "install", "--python", VenvPython, "--require-hashes", "--no-deps", "--no-build", "--strict", "--reinstall", "--compile-bytecode",
                "--default-index", SetupManifest.PackageIndex.ToString(), "--link-mode", "copy", "-r", lockFile).ConfigureAwait(false);

            if (!await ModulesPresentAsync().ConfigureAwait(false) || !await ModulesImportAsync().ConfigureAwait(false))
            {
                throw new SetupException(SetupStepId.Packages, "The packages were installed but do not load. See the log for details.");
            }

            await File.WriteAllTextAsync(marker, _s.Lock.Sha256, ct).ConfigureAwait(false);

            // The downloaded wheels are no longer needed (about as large as the environment itself).
            try
            {
                await RunUvAsync(SetupStepId.Packages, "uv's cache could not be emptied.", TimeSpan.FromMinutes(5), "cache", "clean").ConfigureAwait(false);
            }
            catch (SetupException ex)
            {
                owner.Log("Emptying the cache failed (not needed for GlyphLens to work): " + ex.Message);
            }

            _environmentChanged = true;
            return true;
        }

        private async Task<bool> ModulesPresentAsync()
        {
            string modules = string.Join(", ", RequiredModules.Select(m => $"'{m}'"));
            var result = await RunAsync(
                VenvPython,
                ["-I", "-c", $"import importlib.util, sys; missing = [m for m in ({modules}) if importlib.util.find_spec(m) is None]; print('missing: ' + ' '.join(missing)); sys.exit(1 if missing else 0)"],
                TimeSpan.FromMinutes(1),
                owner.Log).ConfigureAwait(false);
            return !result.TimedOut && result.ExitCode == 0;
        }

        private async Task<bool> ModulesImportAsync()
        {
            // The same settings the worker uses before importing PaddleOCR: no model-source check, its cache inside the models folder.
            var environment = new Dictionary<string, string?>
            {
                ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True",
                ["PADDLE_PDX_CACHE_HOME"] = Path.Combine(ModelsDirectory.OcrFolder(_models), ".cache"),
            };
            var result = await RunAsync(
                VenvPython,
                ["-I", "-c", "import ctranslate2, sentencepiece, onnxruntime, cv2, numpy, paddleocr; print('ctranslate2', ctranslate2.__version__, 'onnxruntime', onnxruntime.__version__, 'paddleocr', paddleocr.__version__)"],
                TimeSpan.FromMinutes(5),
                owner.Log,
                environment).ConfigureAwait(false);
            return !result.TimedOut && result.ExitCode == 0;
        }

        private async Task<bool> OcrModelsAsync()
        {
            long total = SetupManifest.OcrBytes;
            long before = 0;
            bool any = false;
            foreach (var file in SetupManifest.OcrFiles)
            {
                string destination = Path.Combine(_models, file.RelativePath);
                any |= await DownloadAsync(SetupStepId.OcrModels, file.File, destination, before, total).ConfigureAwait(false);
                before += file.File.Size;
            }

            return any;
        }

        private async Task<bool> WorkerAsync()
        {
            var result = await _s.WorkerSwitch.UseInterpreterAsync(VenvPython, _environmentChanged, ct).ConfigureAwait(false);
            owner.Log($"The worker runs on {result.InterpreterPath ?? "an unknown interpreter"}{(result.Restarted ? " (restarted)" : string.Empty)}.");
            if (result.InterpreterPath is null || !string.Equals(Path.GetFullPath(result.InterpreterPath), Path.GetFullPath(VenvPython), StringComparison.OrdinalIgnoreCase))
            {
                _warnings.Add("GlyphLens is set to use another Python (GLYPHLENS_PYTHON or a configured path), so the environment installed here is not used. Remove that setting to use it.");
            }

            return result.Restarted;
        }

        private async Task<bool> TranslationModelAsync(SetupStepId step, PinnedFile file, string source)
        {
            if (HasPackage(await _s.Catalog.GetCatalogAsync(ct).ConfigureAwait(false), source))
            {
                return false;
            }

            string archive = Path.Combine(RuntimeDirectory.DownloadsFolder(_runtime), file.Url.Segments[^1]);
            await DownloadAsync(step, file, archive, 0, file.Size).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // The same validated import as the Models page's "Import model…". It takes a second or two and is not interrupted: the worker
            // reads the archive, extracts into a staging folder and moves it into place atomically, so it is all or nothing.
            Report(step, SetupStepState.Running, "Checking and installing the model…");
            var package = await _s.Catalog.ImportPackageAsync(archive, CancellationToken.None).ConfigureAwait(false);
            owner.Log($"Imported {package.PackageName} {package.Version} ({package.From} -> {package.To}).");
            TryDeleteFile(archive);
            if (!string.Equals(package.From, source, StringComparison.OrdinalIgnoreCase) || !string.Equals(package.To, LanguageCode.English, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(step, "The imported package is not the expected language pair.");
            }

            ct.ThrowIfCancellationRequested();
            return true;
        }

        private async Task<bool> CheckAsync()
        {
            var health = await _s.Worker.GetHealthAsync(ct).ConfigureAwait(false);
            var catalog = await _s.Catalog.GetCatalogAsync(ct).ConfigureAwait(false);
            owner.Log($"Worker health: OCR {health.Ocr.ToWireString()}, translation {health.Translation.ToWireString()}, Python {health.PythonVersion ?? "?"}; {catalog.Packages.Count} model(s); models folder {catalog.StoragePath ?? "?"}.");

            var problems = new List<string>();
            if (catalog.StoragePath is { } storage && !string.Equals(Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_models).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("the worker reads models from another folder");
            }

            switch (LocalOcrAdvisor.Classify(health))
            {
                case LocalOcrState.Ready:
                    break;
                case LocalOcrState.ModelsMissing:
                    problems.Add("the worker does not find the OCR models");
                    break;
                default:
                    problems.Add("the worker's Python does not have the OCR packages");
                    break;
            }

            if (health.Translation is not (WorkerComponentState.Installed or WorkerComponentState.Ready))
            {
                problems.Add("the worker reports translation as not available");
            }

            if (!HasPackage(catalog, "zh"))
            {
                problems.Add("the Simplified Chinese model is not listed");
            }

            if (options.IncludeTraditional && !HasPackage(catalog, "zt"))
            {
                problems.Add("the Traditional Chinese model is not listed");
            }

            if (problems.Count > 0)
            {
                throw new SetupException(SetupStepId.Check, "The setup finished, but " + string.Join("; ", problems) + ".");
            }

            return true;
        }

        private static bool HasPackage(ModelCatalog catalog, string source) => catalog.Packages.Any(p =>
            string.Equals(p.From, source, StringComparison.OrdinalIgnoreCase) && string.Equals(p.To, LanguageCode.English, StringComparison.OrdinalIgnoreCase));

        // ---- helpers ----------------------------------------------------------------------------------------------------

        private async Task<bool> DownloadAsync(SetupStepId step, PinnedFile file, string destination, long before, long total)
        {
            owner.Log(string.Create(CultureInfo.InvariantCulture, $"Download {file.Url} ({file.Size} bytes, SHA-256 {file.Sha256}) to {destination}"));
            var reporter = new DownloadProgress(this, step, file, before, total);
            bool downloaded = await _s.Downloader.DownloadAsync(file, destination, reporter, ct).ConfigureAwait(false);
            owner.Log(downloaded ? $"Downloaded and verified {file.Id}." : $"{file.Id} is already there and verifies; not downloaded.");
            return downloaded;
        }

        private async Task RunUvAsync(SetupStepId step, string failure, TimeSpan timeout, params string[] arguments)
        {
            string[] all = [.. arguments, "--no-config", "--no-progress", "--color", "never"];
            var result = await RunAsync(Uv, all, timeout, owner.Log, UvEnvironment()).ConfigureAwait(false);
            if (result.TimedOut)
            {
                throw new SetupException(step, failure + " It took too long and was stopped.");
            }

            if (result.ExitCode != 0)
            {
                throw new SetupException(step, string.Create(CultureInfo.InvariantCulture, $"{failure} (uv exit code {result.ExitCode}; the log has the details)"));
            }
        }

        private Dictionary<string, string?> UvEnvironment()
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UV_PYTHON_INSTALL_DIR"] = RuntimeDirectory.PythonInstallFolder(_runtime),
                ["UV_PYTHON_BIN_DIR"] = Path.Combine(RuntimeDirectory.PythonInstallFolder(_runtime), "bin"),
                ["UV_CACHE_DIR"] = RuntimeDirectory.CacheFolder(_runtime),
                ["UV_NO_CONFIG"] = "1",
                ["UV_PYTHON_DOWNLOADS"] = "automatic",
                ["UV_MANAGED_PYTHON"] = "1",
                ["UV_LINK_MODE"] = "copy",
                ["UV_NO_PROGRESS"] = "1",
                ["NO_COLOR"] = "1",
            };

            // uv reads proxies only from the environment; hand it the system proxy unless the user already set one.
            if (string.IsNullOrEmpty(_s.GetEnvironmentVariable("HTTPS_PROXY")) && string.IsNullOrEmpty(_s.GetEnvironmentVariable("ALL_PROXY"))
                && _s.ProxyFor(SetupManifest.PackageIndex) is { } proxy)
            {
                environment["HTTPS_PROXY"] = proxy.ToString();
                owner.Log("Using the system proxy for uv.");
            }

            return environment;
        }

        private Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> output, IReadOnlyDictionary<string, string?>? environment = null)
        {
            owner.Log("> " + Path.GetFileName(fileName) + " " + string.Join(" ", arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));
            var request = new ProcessRunRequest(fileName, arguments)
            {
                WorkingDirectory = _runtime,
                Environment = environment ?? new Dictionary<string, string?>(),
                RemoveEnvironmentPrefixes = RemovedPrefixes,
                Timeout = timeout,
            };
            return _s.Processes.RunAsync(request, output, ct);
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner.Log($"Could not delete {Path.GetFileName(path)} ({ex.GetType().Name}).");
            }
        }

        /// <summary>Turns a download's byte count into a step progress report.</summary>
        /// <remarks>Reports at most about 200 times per file, so the user interface is not flooded with updates.</remarks>
        private sealed class DownloadProgress(Run run, SetupStepId step, PinnedFile file, long before, long total) : IProgress<long>
        {
            private readonly long _stride = Math.Max(1, file.Size / 200);
            private long _last = -1;

            public void Report(long value)
            {
                if (_last >= 0 && value - _last < _stride && value != file.Size)
                {
                    return;
                }

                _last = value;
                run.Report(step, SetupStepState.Running, $"Downloading {file.DisplayName} from {file.Host}…", before + value, total);
            }
        }
    }
}
