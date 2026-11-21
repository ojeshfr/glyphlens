using System.Diagnostics;
using System.Globalization;
using GlyphLens.Core.Models;
using GlyphLens.Core.Setup;

namespace GlyphLens.Windows.Worker;

/// <summary>A validated Python interpreter.</summary>
public sealed record PythonInterpreter(string Path, Version Version);

/// <summary>
/// Finds a usable Python interpreter and the worker package.
/// </summary>
/// <remarks>
/// <para>
/// Interpreter search order: an explicitly configured path, then <c>GLYPHLENS_PYTHON</c>, then the environment the one-click setup
/// installs (<c>&lt;runtime&gt;\venv</c>, see <see cref="RuntimeDirectory"/>), then the virtual environment next to the worker package
/// (<c>worker\.venv</c>, where the engines are installed for development), then <c>python.exe</c> on <c>PATH</c> (skipping the
/// Microsoft Store stub under <c>WindowsApps</c>, which opens the Store instead of running Python), then the <c>py</c> launcher. A
/// configured path is never silently replaced by another interpreter. Every candidate is executed once to confirm it really is Python
/// 3.11 or newer.
/// </para>
/// <para>Nothing here installs software or touches the network.</para>
/// </remarks>
public static class WorkerLocator
{
    public const string PythonEnvironmentVariable = "GLYPHLENS_PYTHON";
    public const string WorkerDirectoryEnvironmentVariable = "GLYPHLENS_WORKER_DIR";
    public static readonly Version MinimumPythonVersion = new(3, 11);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public static PythonInterpreter FindPython(string? configuredPath = null) =>
        FindPython(configuredPath, Environment.GetEnvironmentVariable);

    /// <summary><see cref="FindPython(string?)"/> with the environment injected (tests).</summary>
    internal static PythonInterpreter FindPython(string? configuredPath, Func<string, string?> getEnvironmentVariable)
    {
        var configured = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : getEnvironmentVariable(PythonEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Validate(configured) ?? throw new WorkerException(
                WorkerErrorKind.PythonNotFound,
                $"The configured Python interpreter '{configured}' is missing or is not Python {MinimumPythonVersion} or newer.");
        }

        foreach (var candidate in AutomaticCandidates(getEnvironmentVariable))
        {
            if (Validate(candidate) is { } found)
            {
                return found;
            }
        }

        throw new WorkerException(
            WorkerErrorKind.PythonNotFound,
            $"No Python {MinimumPythonVersion} or newer was found. Use \"Install everything\" on the Overview page, install Python, or set {PythonEnvironmentVariable} to its python.exe.");
    }

    /// <summary>
    /// The interpreters tried when none is configured, in order (lazily: the <c>py</c> launcher is only asked when everything before it
    /// failed): the setup's environment, <c>worker\.venv</c>, <c>PATH</c>, the <c>py</c> launcher. Only files that exist are returned.
    /// </summary>
    internal static IEnumerable<string> AutomaticCandidates(Func<string, string?> getEnvironmentVariable)
    {
        if (ManagedRuntimeInterpreter(getEnvironmentVariable) is { } managed)
        {
            yield return managed;
        }

        if (WorkerVirtualEnvironmentInterpreter(getEnvironmentVariable) is { } venv)
        {
            yield return venv;
        }

        foreach (var candidate in PathCandidates(getEnvironmentVariable))
        {
            yield return candidate;
        }

        if (ResolveViaPyLauncher() is { } viaLauncher)
        {
            yield return viaLauncher;
        }
    }

    /// <summary>The interpreter of the setup's environment (<c>&lt;runtime&gt;\venv</c>) when it exists, otherwise <see langword="null"/>.</summary>
    internal static string? ManagedRuntimeInterpreter(Func<string, string?> getEnvironmentVariable)
    {
        var runtime = RuntimeDirectory.Resolve(getEnvironmentVariable);
        if (runtime.Root is null)
        {
            return null; // no usable runtime folder (an invalid override is reported by the setup, not used here)
        }

        var candidate = RuntimeDirectory.VirtualEnvironmentPython(runtime.Root);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Returns the directory that contains the <c>glyphlens_worker</c> package.</summary>
    public static string FindWorkerDirectory(string? configuredDirectory = null)
    {
        var configured = !string.IsNullOrWhiteSpace(configuredDirectory)
            ? configuredDirectory
            : Environment.GetEnvironmentVariable(WorkerDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return IsWorkerDirectory(configured)
                ? System.IO.Path.GetFullPath(configured)
                : throw new WorkerException(WorkerErrorKind.WorkerNotFound, $"'{configured}' does not contain the glyphlens_worker package.");
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            {
                foreach (var candidate in new[] { directory.FullName, System.IO.Path.Combine(directory.FullName, "worker") })
                {
                    if (IsWorkerDirectory(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new WorkerException(
            WorkerErrorKind.WorkerNotFound,
            $"The worker package was not found. Set {WorkerDirectoryEnvironmentVariable} to the directory containing glyphlens_worker.");
    }

    /// <summary>The interpreter of <c>&lt;worker&gt;\.venv</c> when that environment exists, otherwise <see langword="null"/>.</summary>
    private static string? WorkerVirtualEnvironmentInterpreter(Func<string, string?> getEnvironmentVariable)
    {
        try
        {
            var candidate = System.IO.Path.Combine(FindWorkerDirectory(getEnvironmentVariable(WorkerDirectoryEnvironmentVariable)), ".venv", "Scripts", "python.exe");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (WorkerException)
        {
            return null; // no worker package to look next to
        }
    }

    private static bool IsWorkerDirectory(string path) =>
        File.Exists(System.IO.Path.Combine(path, "glyphlens_worker", "__main__.py"));

    private static IEnumerable<string> PathCandidates(Func<string, string?> getEnvironmentVariable)
    {
        var path = getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = System.IO.Path.Combine(entry, "python.exe");
            }
            catch (ArgumentException)
            {
                continue; // an invalid PATH entry
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? ResolveViaPyLauncher()
    {
        var output = RunAndCapture("py.exe", ["-3", "-c", "import sys; print(sys.executable)"]);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static PythonInterpreter? Validate(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var output = RunAndCapture(path, ["-c", "import sys; print('%d.%d' % sys.version_info[:2])"]);
        if (output is null)
        {
            return null;
        }

        var parts = output.Trim().Split('.');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            var version = new Version(major, minor);
            if (version >= MinimumPythonVersion)
            {
                return new PythonInterpreter(System.IO.Path.GetFullPath(path), version);
            }
        }

        return null;
    }

    private static string? RunAndCapture(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
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

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // not installed or not executable
        }
    }
}
