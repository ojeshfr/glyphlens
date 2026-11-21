using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlyphLens.App.Services;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;
using GlyphLens.Core.Setup;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Infrastructure.Ocr;
using GlyphLens.Infrastructure.Setup;
using GlyphLens.Infrastructure.Translation;
using GlyphLens.Windows.Ocr;
using GlyphLens.Windows.Setup;
using GlyphLens.Windows.Worker;
using Xunit.Abstractions;

namespace GlyphLens.App.Tests;

/// <summary>Runs only when <c>GLYPHLENS_RUN_INSTALL_TESTS=1</c> (downloads about 360 MB on a fresh folder; never run by CI).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InstallChainFactAttribute : FactAttribute
{
    public InstallChainFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GLYPHLENS_RUN_INSTALL_TESTS") != "1")
        {
            Skip = "Installs the real environment and runs the real worker (needs the internet on a fresh folder). Set GLYPHLENS_RUN_INSTALL_TESTS=1 to run.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealChainTestGroup
{
    public const string Name = "Real Chinese chain (opt-in, changes the process environment)";
}

/// <summary>
/// The whole chain a friend gets, with the production classes and no fakes: no usable Python at first, "Install everything" (the
/// real installer with the application's own <see cref="WorkerEnvironmentSwitch"/>), the worker found by <see cref="WorkerLocator"/>
/// in the managed environment, then a real OCR request on an image of Chinese text and real zh-to-en translations through the C#
/// clients. Also: a worker already running on another interpreter is restarted onto the managed one. Folders:
/// <c>GLYPHLENS_INSTALL_TEST_ROOT</c> (default <c>%TEMP%\glyphlens-install-test</c>) <c>\runtime</c> and <c>\models</c>.
/// </summary>
[Collection(RealChainTestGroup.Name)]
public sealed class RealChineseChainTests(ITestOutputHelper output)
{
    private const string Sentence = "你今天晚上有空吗？我们一起去吃饭吧。";

    private static readonly string[] Variables =
        ["PATH", WorkerLocator.PythonEnvironmentVariable, WorkerLocator.WorkerDirectoryEnvironmentVariable, RuntimeDirectory.EnvironmentVariable, ModelsDirectory.EnvironmentVariable];

    [InstallChainFact]
    public async Task FromNoUsablePython_InstallEverything_ThenRealOcrAndTranslationOfChinese()
    {
        string root = Environment.GetEnvironmentVariable("GLYPHLENS_INSTALL_TEST_ROOT") is { Length: > 0 } r ? r : Path.Combine(Path.GetTempPath(), "glyphlens-install-test");
        string runtime = Path.Combine(root, "runtime");
        string models = Path.Combine(root, "models");
        string emptyRuntime = Path.Combine(root, "no-runtime");
        string workerCopy = Path.Combine(root, "worker-source");
        var saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

        // Another (usable, but engine-less or dev) interpreter, found the normal way before the environment is changed.
        Environment.SetEnvironmentVariable(RuntimeDirectory.EnvironmentVariable, emptyRuntime);
        string? other = TryFindPython();
        string sourceWorker = WorkerLocator.FindWorkerDirectory();
        CopyWorkerSource(Path.Combine(sourceWorker, "glyphlens_worker"), Path.Combine(workerCopy, "glyphlens_worker"));

        string systemOnly = string.Join(Path.PathSeparator, Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var launcher = new PythonWorkerProcessLauncher(); // no explicit interpreter: WorkerLocator decides, as in the app
        var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher);
        try
        {
            // 1. A machine without a usable Python: no GLYPHLENS_PYTHON, no Python on PATH, no worker\.venv, no managed runtime.
            Environment.SetEnvironmentVariable(WorkerLocator.PythonEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(WorkerLocator.WorkerDirectoryEnvironmentVariable, workerCopy);
            Environment.SetEnvironmentVariable("PATH", systemOnly);
            Environment.SetEnvironmentVariable(ModelsDirectory.EnvironmentVariable, models);
            var noPython = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));
            output.WriteLine($"Before the setup: {manager.Status}: {noPython.Message}");
            Assert.Equal(WorkerStatus.Unavailable, manager.Status);

            // 2. "Install everything" with the application's own worker switch.
            Environment.SetEnvironmentVariable(RuntimeDirectory.EnvironmentVariable, runtime);
            var catalog = new WorkerModelCatalog(manager);
            using var downloader = new HttpFileDownloader("0.0.0-test");
            var appSwitch = new WorkerEnvironmentSwitch(manager, () => launcher.InterpreterPath, launcher.ForgetInterpreter);
            var installer = new EnvironmentInstaller(new EnvironmentInstallerServices(
                RuntimeDirectory.Resolve(Environment.GetEnvironmentVariable),
                ModelsDirectory.Resolve(Environment.GetEnvironmentVariable),
                downloader,
                new JobProcessRunner(),
                catalog,
                manager,
                appSwitch,
                new SetupLogFile(RuntimeDirectory.LogFile(runtime)),
                EngineLock.LoadEmbedded()));
            var result = await installer.RunAsync(new SetupOptions(IncludeTraditional: true), null, CancellationToken.None);
            output.WriteLine($"Setup: skipped {string.Join(", ", result.Skipped)}; worker on {launcher.InterpreterPath}.");
            Assert.Empty(result.Warnings);
            Assert.Equal(WorkerStatus.Ready, manager.Status);
            Assert.Equal(RuntimeDirectory.VirtualEnvironmentPython(runtime), launcher.InterpreterPath, ignoreCase: true);

            await ProveChineseWorksAsync(manager);

            // 3. A worker already running on another interpreter is restarted onto the managed environment.
            if (other is null)
            {
                output.WriteLine("No other Python on this machine: the restart case was not exercised.");
                Assert.Fail("The restart case needs another Python 3.11+ on this machine.");
            }

            await manager.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable(RuntimeDirectory.EnvironmentVariable, emptyRuntime);
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, systemOnly, Path.GetDirectoryName(other)));
            launcher.ForgetInterpreter();
            await manager.StartAsync(CancellationToken.None);
            output.WriteLine($"Running on another interpreter: {launcher.InterpreterPath} (Python {manager.LastHealth?.PythonVersion}).");
            Assert.Equal(other, launcher.InterpreterPath, ignoreCase: true);

            Environment.SetEnvironmentVariable(RuntimeDirectory.EnvironmentVariable, runtime);
            var switched = await appSwitch.UseInterpreterAsync(RuntimeDirectory.VirtualEnvironmentPython(runtime), restart: false, CancellationToken.None);
            var health = await manager.GetHealthAsync(CancellationToken.None);
            output.WriteLine($"After the switch: {switched.InterpreterPath}, restarted {switched.Restarted}, Python {health.PythonVersion}, OCR {health.Ocr.ToWireString()}.");
            Assert.True(switched.Restarted);
            Assert.Equal(RuntimeDirectory.VirtualEnvironmentPython(runtime), switched.InterpreterPath, ignoreCase: true);
            Assert.Equal(LocalOcrState.Ready, LocalOcrAdvisor.Classify(health));

            // Already on the managed interpreter: no restart.
            var again = await appSwitch.UseInterpreterAsync(RuntimeDirectory.VirtualEnvironmentPython(runtime), restart: false, CancellationToken.None);
            Assert.False(again.Restarted);
        }
        finally
        {
            await manager.DisposeAsync();
            launcher.Dispose();
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private async Task ProveChineseWorksAsync(WorkerProcessManager manager)
    {
        var health = await manager.GetHealthAsync(CancellationToken.None);
        output.WriteLine($"Worker health: Python {health.PythonVersion}, OCR {health.Ocr.ToWireString()}, translation {health.Translation.ToWireString()}.");
        Assert.StartsWith("3.12", health.PythonVersion, StringComparison.Ordinal);
        Assert.Equal(LocalOcrState.Ready, LocalOcrAdvisor.Classify(health));

        // Real OCR of rendered Chinese text through the C# client (PNG over the pipe).
        using var ocr = new WorkerOcrEngine(manager, new PngFrameEncoder());
        using var frame = RenderText(Sentence);
        var regions = await ocr.RecognizeAsync(frame, CancellationToken.None);
        string read = string.Concat(regions.Select(x => x.Text));
        output.WriteLine($"OCR read: {read} ({regions.Count} line(s), confidence {(regions.Count > 0 ? regions[0].Confidence : null):0.00})");
        Assert.Contains("吃饭", read, StringComparison.Ordinal);
        Assert.Contains("晚上", read, StringComparison.Ordinal);

        // Real zh -> en translation through the C# client, of the sentence and of what OCR read.
        using var translation = new WorkerTranslationEngine(manager);
        foreach (string text in new[] { Sentence, read })
        {
            var translated = await translation.TranslateAsync(new TranslationRequest(text, "zh", "en"), CancellationToken.None);
            output.WriteLine($"zh -> en: {text} => {translated.TranslatedText}");
            string english = translated.TranslatedText.ToLowerInvariant();
            Assert.True(english.Contains("free", StringComparison.Ordinal) || english.Contains("tonight", StringComparison.Ordinal), english);
            Assert.True(english.Contains("eat", StringComparison.Ordinal) || english.Contains("dinner", StringComparison.Ordinal), english);
        }

        var traditional = await translation.TranslateAsync(new TranslationRequest("你今天晚上有空嗎？我們一起去吃飯吧。", "zt", "en"), CancellationToken.None);
        output.WriteLine($"zt -> en: {traditional.TranslatedText}");
        Assert.Matches("[A-Za-z]{3,}", traditional.TranslatedText);
    }

    private static string? TryFindPython()
    {
        try
        {
            return WorkerLocator.FindPython().Path;
        }
        catch (WorkerException)
        {
            return null;
        }
    }

    private static void CopyWorkerSource(string from, string to)
    {
        if (Directory.Exists(to))
        {
            Directory.Delete(to, recursive: true);
        }

        foreach (string file in Directory.EnumerateFiles(from, "*.py", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Draws black Chinese text on white with WPF (on an STA thread) and returns it as a BGRA frame.</summary>
    private static CapturedFrame RenderText(string text)
    {
        CapturedFrame? frame = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                const int width = 760, height = 90;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 32, Brushes.Black, 1.0);
                    dc.DrawText(formatted, new Point(20, 22));
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                byte[] pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                var metadata = new FrameMetadata("window:1", width, height, width * 4, GlyphLens.Core.Models.PixelFormat.Bgra8, DateTimeOffset.UtcNow, 1);
                frame = new CapturedFrame(metadata, pixels);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return frame ?? throw new InvalidOperationException("Rendering failed.", failure);
    }
}
