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
