using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GlyphLens.Core.Abstractions;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Setup;

/// <summary>
/// Runs the setup's child processes (uv and Python checks): no window, no shell, arguments passed one by one, standard input closed
/// (so nothing can wait for an answer), output streamed line by line. Each process is placed in its own kill-on-close job object, so
/// a cancellation, a timeout or the application exiting ends it and everything it started (the whole process tree).
/// </summary>
public sealed class JobProcessRunner : IProcessRunner
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, Action<string>? outputLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.FileName) ?? string.Empty,
        };
        foreach (string argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string name in info.Environment.Keys.ToList())
        {
            if (request.RemoveEnvironmentPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                _ = info.Environment.Remove(name);
            }
        }

        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
            {
                _ = info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        using var job = new WorkerJobObject();
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Forward(outputLine, e.Data);
        process.ErrorDataReceived += (_, e) => Forward(outputLine, e.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"{Path.GetFileName(request.FileName)} did not start.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"{Path.GetFileName(request.FileName)} could not be started: {ex.Message}", ex);
        }

        try
        {
            job.Assign(process);
        }
        catch (Win32Exception)
        {
            // Not fatal: cancellation still kills the whole process tree below.
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            job.Dispose(); // ends anything the process started that is still alive
            using var wait = new CancellationTokenSource(KillWait);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Killed but its output handles are still open somewhere; the job is closed, nothing of it keeps running.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessRunResult(-1, TimedOut: true);
        }
    }

    private static void Forward(Action<string>? outputLine, string? line)
    {
        if (line is not null)
        {
            outputLine?.Invoke(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }
}
