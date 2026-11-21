using GlyphLens.Core.Setup;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// The one-click environment setup ("Install everything"). It runs only when the user starts it; nothing here runs at start-up.
/// </summary>
public interface IEnvironmentSetup
{
    /// <summary>What a run with <paramref name="options"/> would download and where it would install (for the consent text).</summary>
    /// <exception cref="SetupException">No usable runtime or models folder.</exception>
    SetupPlan Plan(SetupOptions options);

    /// <summary>
    /// Runs the steps. A step that is already in place and verifies is skipped, so running it again resumes where a failed or cancelled
    /// run stopped. Cancelling stops child processes and deletes partial files.
    /// </summary>
    /// <exception cref="SetupException">A step failed; the message says which and why.</exception>
    /// <exception cref="OperationCanceledException">The user cancelled.</exception>
    Task<SetupResult> RunAsync(SetupOptions options, IProgress<SetupProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Downloads a pinned file over HTTPS and keeps it only if its size and SHA-256 match.</summary>
public interface IFileDownloader
{
    /// <summary>
    /// Makes <paramref name="destination"/> hold exactly <paramref name="file"/>. When it already exists and verifies, nothing is
    /// downloaded and <see langword="false"/> is returned. Otherwise the file is streamed to <c>&lt;destination&gt;.part</c>, checked
    /// while it arrives (it is abandoned as soon as it is larger than pinned), verified, and moved into place.
    /// </summary>
    /// <param name="file">The pinned file.</param>
    /// <param name="destination">Where it belongs.</param>
    /// <param name="progress">Receives the number of bytes received so far.</param>
    /// <param name="cancellationToken">Stops the download and deletes the partial file.</param>
    /// <returns><see langword="true"/> when the file was downloaded, <see langword="false"/> when it was already there.</returns>
    /// <exception cref="DownloadException">The download failed or did not verify; no file is left behind.</exception>
    Task<bool> DownloadAsync(PinnedFile file, string destination, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>A child process to run, with its arguments and environment.</summary>
/// <param name="FileName">The executable (a full path).</param>
/// <param name="Arguments">The arguments, passed one by one (never through a shell).</param>
public sealed record ProcessRunRequest(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The working directory, or <see langword="null"/> for the executable's folder.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables to set; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Inherited variables whose names start with one of these prefixes (case-insensitive) are removed first.</summary>
    public IReadOnlyList<string> RemoveEnvironmentPrefixes { get; init; } = [];

    /// <summary>The process (and everything it started) is killed after this long.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>How a child process ended.</summary>
/// <param name="ExitCode">Its exit code (meaningless when <paramref name="TimedOut"/>).</param>
/// <param name="TimedOut">It ran longer than <see cref="ProcessRunRequest.Timeout"/> and was killed.</param>
public sealed record ProcessRunResult(int ExitCode, bool TimedOut);

/// <summary>Runs child processes for the setup, with no window, and makes sure none of them outlives a cancellation.</summary>
public interface IProcessRunner
{
    /// <summary>Runs the process to the end and returns how it ended. Standard output and error are passed to <paramref name="outputLine"/>.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; the process and all its children were killed first.</exception>
    /// <exception cref="InvalidOperationException">The executable could not be started.</exception>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, Action<string>? outputLine, CancellationToken cancellationToken);
}

/// <summary>Whether the worker now runs on a given interpreter, after <see cref="IWorkerEnvironmentSwitch.UseInterpreterAsync"/>.</summary>
/// <param name="InterpreterPath">The interpreter the worker runs on, or <see langword="null"/> when it is not known.</param>
/// <param name="Restarted">Whether the worker was started or restarted to get there.</param>
public sealed record WorkerSwitchResult(string? InterpreterPath, bool Restarted);

/// <summary>Moves the worker onto the interpreter the setup installed (implemented by the application, which owns the worker).</summary>
public interface IWorkerEnvironmentSwitch
{
    /// <summary>
    /// Forgets the interpreter found earlier and (re)starts the worker when it does not already run on <paramref name="pythonPath"/>, or
    /// always when <paramref name="restart"/> is set (the environment changed under a running worker). The worker picks its interpreter
    /// by the usual order, so a configured Python (<c>GLYPHLENS_PYTHON</c>) still wins.
    /// </summary>
    /// <exception cref="Models.WorkerException">The worker could not be started.</exception>
    Task<WorkerSwitchResult> UseInterpreterAsync(string pythonPath, bool restart, CancellationToken cancellationToken);
}

/// <summary>The setup's technical log: commands and installer output only, never screen content.</summary>
public interface ISetupLog
{
    /// <summary>The log file, or <see langword="null"/> when the log is not written to a file.</summary>
    string? FilePath { get; }

    void Write(string line);
}

/// <summary>A setup log that writes nothing (when there is no runtime folder to write it to).</summary>
public sealed class NullSetupLog : ISetupLog
{
    public static readonly NullSetupLog Instance = new();

    public string? FilePath => null;

    public void Write(string line)
    {
    }
}
