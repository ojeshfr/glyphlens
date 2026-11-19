using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Windows.Worker;

/// <summary>Settings for <see cref="PythonWorkerProcessLauncher"/>. Unset values fall back to discovery.</summary>
public sealed record PythonWorkerLaunchOptions
{
    /// <summary>Explicit path to <c>python.exe</c>.</summary>
    public string? InterpreterPath { get; init; }

    /// <summary>Directory containing the <c>glyphlens_worker</c> package.</summary>
    public string? WorkerDirectory { get; init; }
}

/// <summary>
/// Starts the Python worker (<c>python -m glyphlens_worker --pipe NAME</c>) inside a kill-on-close job object.
/// </summary>
/// <remarks>
/// <para>
/// Hardening: the interpreter runs with <c>-E</c> (ignores <c>PYTHONPATH</c>/<c>PYTHONHOME</c>, so the environment cannot
/// redirect it to other code) and <c>-X utf8</c>. The session token travels in the environment, not on the command line
/// (command lines are visible to other processes). Standard output and error are redirected and drained so a chatty worker
/// can never block, and the last few error lines are kept (sanitised) for diagnostics.
/// </para>
/// <para>The worker is started with no console window and inherits no handles beyond the redirected streams.</para>
/// </remarks>
public sealed class PythonWorkerProcessLauncher : IWorkerProcessLauncher, IDisposable
{
    /// <summary>Environment variable that carries the session token to the worker.</summary>
    public const string TokenEnvironmentVariable = "GLYPHLENS_WORKER_TOKEN";

    private readonly PythonWorkerLaunchOptions _options;
    private readonly object _gate = new();
    private WorkerJobObject? _job;
    private PythonInterpreter? _interpreter;
    private string? _workerDirectory;
    private bool _disposed;

    public PythonWorkerProcessLauncher(PythonWorkerLaunchOptions? options = null)
    {
        _options = options ?? new PythonWorkerLaunchOptions();
    }

    public IWorkerProcess Launch(WorkerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        PythonInterpreter interpreter;
        string workerDirectory;
        WorkerJobObject job;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _interpreter ??= WorkerLocator.FindPython(_options.InterpreterPath);
            _workerDirectory ??= WorkerLocator.FindWorkerDirectory(_options.WorkerDirectory);
            _job ??= CreateJob();
            interpreter = _interpreter;
            workerDirectory = _workerDirectory;
            job = _job;
        }

        var info = new ProcessStartInfo(interpreter.Path)
        {
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-E", "-X", "utf8", "-u", "-m", "glyphlens_worker", "--pipe", spec.PipeName })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment[TokenEnvironmentVariable] = spec.SessionToken;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var handle = new WorkerProcessHandle(process);
        try
        {
            if (!process.Start())
            {
                throw new WorkerException(WorkerErrorKind.LaunchFailed, "The worker process did not start.");
            }

            handle.BeginObserving();
            job.Assign(process);
            return handle;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            handle.Dispose();
            throw new WorkerException(WorkerErrorKind.LaunchFailed, "The worker process could not be started: " + ex.Message, null, ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _job?.Dispose(); // kills any worker still running
            _job = null;
        }
    }

    private static WorkerJobObject CreateJob()
    {
        try
        {
            return new WorkerJobObject();
        }
        catch (Win32Exception ex)
        {
            throw new WorkerException(WorkerErrorKind.LaunchFailed, "The worker could not be tied to the application's lifetime: " + ex.Message, null, ex);
        }
    }
}

/// <summary>A started worker process with drained output and an exit task.</summary>
internal sealed class WorkerProcessHandle : IWorkerProcess
{
    private const int MaxErrorLines = 20;
    private const int MaxLineLength = 300;
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _errorLines = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _id;
    private int _disposed;

    public WorkerProcessHandle(Process process)
    {
        _process = process;
        process.ErrorDataReceived += (_, e) => RecordError(e.Data);
        process.OutputDataReceived += static (_, _) => { }; // drained and discarded
        process.Exited += (_, _) => _ = CompleteAsync();
    }

    public int Id => _id;

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<string> RecentErrorOutput => [.. _errorLines];

    public void BeginObserving()
    {
        _id = _process.Id;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Not started or already exited.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Never started.
        }

        _process.Dispose();
    }

    private async Task CompleteAsync()
    {
        try
        {
            // Let redirected output finish so the error tail is complete, but never wait forever on a grandchild
            // that still holds the pipe.
            using var cts = new CancellationTokenSource(OutputDrainTimeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Output did not close in time; the exit code is still valid.
        }
        catch (InvalidOperationException)
        {
            // Disposed concurrently.
        }

        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        _exited.TrySetResult(code);
    }

    private void RecordError(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var clean = new string(line.Select(c => char.IsControl(c) ? ' ' : c).Take(MaxLineLength).ToArray());
        _errorLines.Enqueue(clean);
        while (_errorLines.Count > MaxErrorLines && _errorLines.TryDequeue(out _))
        {
        }
    }
}
