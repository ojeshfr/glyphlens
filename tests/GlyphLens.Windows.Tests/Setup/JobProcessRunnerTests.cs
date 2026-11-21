using System.Diagnostics;
using GlyphLens.Core.Abstractions;
using GlyphLens.Windows.Setup;

namespace GlyphLens.Windows.Tests.Setup;

/// <summary>The setup's process runner against real processes (cmd.exe and ping.exe from System32; no network).</summary>
[Collection(RealInstallTestGroup.Name)] // not in parallel with other tests that start ping.exe, so the kill check counts only ours
public sealed class JobProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ProcessRunRequest CmdRequest(string command, TimeSpan? timeout = null) =>
        new(Cmd, ["/d", "/c", command]) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task OutputLines_AndTheExitCode_AreReported()
    {
        var lines = new List<string>();

        var result = await new JobProcessRunner().RunAsync(CmdRequest("echo first&& echo second 1>&2&& exit /b 3"), l => { lock (lines) { lines.Add(l.Trim()); } }, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("first", lines);
        Assert.Contains("second", lines);
    }

    [Fact]
    public async Task InheritedVariables_WithARemovedPrefix_AreNotPassedOn_AndSetOnesAre()
    {
        var lines = new List<string>();
        var request = CmdRequest("echo [%COMPUTERNAME%] [%GLYPHLENS_TEST_SET%]") with
        {
            RemoveEnvironmentPrefixes = ["COMPUTERNAME"],
            Environment = new Dictionary<string, string?> { ["GLYPHLENS_TEST_SET"] = "yes" },
        };

        await new JobProcessRunner().RunAsync(request, lines.Add, CancellationToken.None);

        Assert.Equal("[%COMPUTERNAME%] [yes]", Assert.Single(lines).Trim());
    }

    [Fact]
    public async Task StandardInput_IsClosed_SoNothingCanWaitForAnAnswer()
    {
        var result = await new JobProcessRunner().RunAsync(CmdRequest("set /p answer=question? "), null, CancellationToken.None);

        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task AProcessThatRunsTooLong_IsKilled_AndReportedAsTimedOut()
    {
        var watch = Stopwatch.StartNew();

        var result = await new JobProcessRunner().RunAsync(CmdRequest("ping -n 60 127.0.0.1 >nul", TimeSpan.FromSeconds(1)), null, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancelling_KillsTheWholeProcessTree()
    {
        var before = PingIds();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // cmd.exe starts ping.exe (a grandchild of this test): both must be gone after the cancellation.
        var run = new JobProcessRunner().RunAsync(CmdRequest("ping -n 60 127.0.0.1"), _ => started.TrySetResult(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var during = PingIds().Except(before).ToList();
        Assert.NotEmpty(during);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Task.Delay(500);
        Assert.Empty(PingIds().Intersect(during));
    }

    [Fact]
    public async Task AMissingExecutable_IsReportedAsInvalidOperation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JobProcessRunner().RunAsync(new ProcessRunRequest(@"C:\no\such\tool.exe", []), null, CancellationToken.None));
    }

    private static HashSet<int> PingIds()
    {
        var processes = Process.GetProcessesByName("PING");
        try
        {
            return [.. processes.Select(p => p.Id)];
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
