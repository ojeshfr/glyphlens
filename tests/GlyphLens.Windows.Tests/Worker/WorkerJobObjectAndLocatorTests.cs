using System.Diagnostics;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Tests.Worker;

public class WorkerJobObjectTests
{
    private static Process StartLongRunning()
    {
        var info = new ProcessStartInfo("ping.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("120");
        info.ArgumentList.Add("127.0.0.1");
        return Process.Start(info)!;
    }

    [Fact]
    public void ClosingTheJob_KillsTheProcessesInIt()
    {
        using var process = StartLongRunning();
        var job = new WorkerJobObject();
        job.Assign(process);
        Assert.False(process.HasExited);

        job.Dispose(); // what Windows does when the application dies: the last handle closes

        Assert.True(process.WaitForExit(10_000), "KILL_ON_JOB_CLOSE must terminate the process.");
    }

    [Fact]
    public void AProcessNotAssignedToTheJob_IsNotAffected()
    {
        using var bystander = StartLongRunning();
        try
        {
            using var assigned = StartLongRunning();
            var job = new WorkerJobObject();
            job.Assign(assigned);

            job.Dispose();

            Assert.True(assigned.WaitForExit(10_000));
            Assert.False(bystander.HasExited);
        }
        finally
        {
            bystander.Kill();
        }
    }

    [Fact]
    public void Dispose_IsIdempotent_AndAssignAfterDisposeIsRefused()
    {
        var job = new WorkerJobObject();
        job.Dispose();
        job.Dispose();
        using var process = StartLongRunning();
        try
        {
            Assert.Throws<ObjectDisposedException>(() => job.Assign(process));
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public void Assign_RejectsNull()
    {
        using var job = new WorkerJobObject();

        Assert.Throws<ArgumentNullException>(() => job.Assign(null!));
    }
}

public class WorkerLocatorTests
{
    [Fact]
    public void FindWorkerDirectory_FindsThePackageFromTheBuildOutput()
    {
        var directory = WorkerLocator.FindWorkerDirectory();

        Assert.True(File.Exists(Path.Combine(directory, "glyphlens_worker", "__main__.py")));
    }

    [Fact]
    public void FindWorkerDirectory_HonoursAnExplicitDirectory_AndRefusesAWrongOne()
    {
        var good = WorkerLocator.FindWorkerDirectory();

        Assert.Equal(Path.GetFullPath(good), WorkerLocator.FindWorkerDirectory(good));
        var ex = Assert.Throws<WorkerException>(() => WorkerLocator.FindWorkerDirectory(Path.GetTempPath()));
        Assert.Equal(WorkerErrorKind.WorkerNotFound, ex.Kind);
    }

    [Fact]
    public void FindPython_NeverSilentlyReplacesAConfiguredInterpreter()
    {
        var ex = Assert.Throws<WorkerException>(() => WorkerLocator.FindPython(@"C:\no\such\place\python.exe"));

        Assert.Equal(WorkerErrorKind.PythonNotFound, ex.Kind);
        Assert.Contains(@"C:\no\such\place\python.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindPython_RejectsAnExecutableThatIsNotPython()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "whoami.exe");

        var ex = Assert.Throws<WorkerException>(() => WorkerLocator.FindPython(notepad));

        Assert.Equal(WorkerErrorKind.PythonNotFound, ex.Kind);
    }

    [WorkerIntegrationFact]
    public void FindPython_ReturnsAValidatedInterpreterOfTheMinimumVersionOrNewer()
    {
        var python = WorkerLocator.FindPython();

        Assert.True(File.Exists(python.Path));
        Assert.True(python.Version >= WorkerLocator.MinimumPythonVersion);
        Assert.DoesNotContain("WindowsApps", python.Path, StringComparison.OrdinalIgnoreCase); // never the Store stub
        Assert.Equal(python.Path, WorkerLocator.FindPython(python.Path).Path);
    }

    [Fact]
    public void MinimumPythonVersion_Is311()
    {
        Assert.Equal(new Version(3, 11), WorkerLocator.MinimumPythonVersion);
    }
}

public sealed class WorkerLocatorOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "glyphlens-locator-" + Guid.NewGuid().ToString("N"));

    public WorkerLocatorOrderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(params string[] parts)
    {
        string path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, string.Empty);
        return path;
    }

    private static Func<string, string?> Env(Dictionary<string, string?> values) => name => values.GetValueOrDefault(name);

    [Fact]
    public void TheManagedRuntime_ComesBeforeWorkerVenv_WhichComesBeforePath()
    {
        string managed = File("runtime", "venv", "Scripts", "python.exe");
        File("worker", "glyphlens_worker", "__main__.py");
        string workerVenv = File("worker", ".venv", "Scripts", "python.exe");
        string onPath = File("bin", "python.exe");
        var env = Env(new()
        {
            [GlyphLens.Core.Setup.RuntimeDirectory.EnvironmentVariable] = Path.Combine(_root, "runtime"),
            [WorkerLocator.WorkerDirectoryEnvironmentVariable] = Path.Combine(_root, "worker"),
            ["PATH"] = Path.Combine(_root, "bin"),
        });

        var candidates = WorkerLocator.AutomaticCandidates(env).Take(3).ToList();

        Assert.Equal([managed, workerVenv, onPath], candidates);
    }

    [Fact]
    public void TheManagedRuntime_DefaultsToLocalAppData()
    {
        string managed = File("LocalAppData", "GlyphLens", "runtime", "venv", "Scripts", "python.exe");

        Assert.Equal(managed, WorkerLocator.ManagedRuntimeInterpreter(Env(new() { ["LOCALAPPDATA"] = Path.Combine(_root, "LocalAppData") })));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData(@"\\server\share\runtime")]
    public void AnUnusableRuntimeOverride_IsNotUsed(string value)
    {
        File("LocalAppData", "GlyphLens", "runtime", "venv", "Scripts", "python.exe");

        Assert.Null(WorkerLocator.ManagedRuntimeInterpreter(Env(new()
        {
            [GlyphLens.Core.Setup.RuntimeDirectory.EnvironmentVariable] = value,
            ["LOCALAPPDATA"] = Path.Combine(_root, "LocalAppData"),
        })));
    }

    [Fact]
    public void AManagedRuntimeWithoutAnEnvironment_IsNotACandidate() =>
        Assert.Null(WorkerLocator.ManagedRuntimeInterpreter(Env(new() { [GlyphLens.Core.Setup.RuntimeDirectory.EnvironmentVariable] = Path.Combine(_root, "empty") })));

    [Fact]
    public void AConfiguredInterpreter_StillWins_AndIsNeverReplaced()
    {
        File("runtime", "venv", "Scripts", "python.exe");
        var env = Env(new()
        {
            [GlyphLens.Core.Setup.RuntimeDirectory.EnvironmentVariable] = Path.Combine(_root, "runtime"),
            [WorkerLocator.PythonEnvironmentVariable] = @"C:\no\such\python.exe",
        });

        var ex = Assert.Throws<WorkerException>(() => WorkerLocator.FindPython(null, env));

        Assert.Contains(@"C:\no\such\python.exe", ex.Message, StringComparison.Ordinal);
    }

    [WorkerIntegrationFact]
    public void ARealManagedVenv_IsFoundAndValidated_BeforeAnyOtherPython()
    {
        string runtime = Path.Combine(_root, "runtime");
        using (var venv = WorkerTestEnvironment.StartPython("-m", "venv", "--without-pip", Path.Combine(runtime, "venv")))
        {
            Assert.True(venv.WaitForExit(120_000));
            Assert.Equal(0, venv.ExitCode);
        }

        var found = WorkerLocator.FindPython(null, Env(new()
        {
            [GlyphLens.Core.Setup.RuntimeDirectory.EnvironmentVariable] = runtime,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
        }));

        Assert.Equal(Path.Combine(runtime, "venv", "Scripts", "python.exe"), found.Path, ignoreCase: true);
    }
}
