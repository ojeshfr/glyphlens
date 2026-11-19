using System.Diagnostics;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Tests.Worker;

/// <summary>
/// Runs a test only when a Python 3.11+ interpreter and the worker package are available. They need no models, GPU or
/// network, so CI (which installs Python) runs them; a machine without Python skips them with a visible reason.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WorkerIntegrationFactAttribute : FactAttribute
{
    public WorkerIntegrationFactAttribute()
    {
        if (WorkerTestEnvironment.UnavailableReason is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class WorkerTestEnvironment
{
    private static readonly Lazy<(PythonInterpreter? Python, string? WorkerDirectory, string? Reason)> Probe = new(() =>
    {
        try
        {
            return (WorkerLocator.FindPython(), WorkerLocator.FindWorkerDirectory(), null);
        }
        catch (WorkerException ex)
        {
            return (null, null, "Worker integration tests need Python 3.11+ and the worker package: " + ex.Message);
        }
    });

    public static string? UnavailableReason => Probe.Value.Reason;

    public static PythonInterpreter Python => Probe.Value.Python ?? throw new InvalidOperationException(UnavailableReason);

    public static string WorkerDirectory => Probe.Value.WorkerDirectory ?? throw new InvalidOperationException(UnavailableReason);

    /// <summary>A launcher for the real worker with explicit, already-resolved paths.</summary>
    public static PythonWorkerProcessLauncher CreateLauncher() =>
        new(new PythonWorkerLaunchOptions { InterpreterPath = Python.Path, WorkerDirectory = WorkerDirectory });

    /// <summary>Starts an arbitrary Python snippet in the worker's job-object-less harness (for "cannot connect" scenarios).</summary>
    public static Process StartPython(params string[] arguments)
    {
        var info = new ProcessStartInfo(Python.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Python did not start.");
    }
}

/// <summary>A launcher decorator that lets a test change what each launch does.</summary>
internal sealed class ScriptedLauncher(Func<int, WorkerLaunchSpec, IWorkerProcess> launch) : IWorkerProcessLauncher
{
    private int _count;

    public int LaunchCount => Volatile.Read(ref _count);

    public IWorkerProcess Launch(WorkerLaunchSpec spec) => launch(Interlocked.Increment(ref _count), spec);
}

/// <summary>Wraps an arbitrary OS process as an <see cref="IWorkerProcess"/> (no job object).</summary>
internal sealed class PlainProcess : IWorkerProcess
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlainProcess(Process process)
    {
        _process = process;
        Id = process.Id;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _exited.TrySetResult(process.ExitCode);
        if (process.HasExited)
        {
            _exited.TrySetResult(process.ExitCode);
        }

        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
    }

    public int Id { get; }

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<string> RecentErrorOutput => [];

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

internal static class ProcessAssert
{
    /// <summary>True when no process with this id is running (or the id was reused by a different start time is not considered).</summary>
    public static bool IsGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
