namespace GlyphLens.Core.Setup;

/// <summary>Where the setup keeps the Python runtime, or why there is no usable place.</summary>
/// <param name="Root">The runtime folder, or <see langword="null"/> when none can be used.</param>
/// <param name="Problem">Why <paramref name="Root"/> is <see langword="null"/> (plain words, no values).</param>
public sealed record RuntimeLocation(string? Root, string? Problem);

/// <summary>
/// The runtime folder that the one-click setup fills: <c>GLYPHLENS_RUNTIME_DIR</c> when it is an absolute local path, otherwise
/// <c>%LOCALAPPDATA%\GlyphLens\runtime</c>. Everything the setup installs lives below this folder (uv, the Python build, the virtual
/// environment, uv's cache while installing, the setup log) except the model files, which go to the models folder.
/// </summary>
/// <remarks>
/// An override that is relative or on the network is refused (and reported), never replaced by the default: the user asked for a
/// different place, so silently writing elsewhere would surprise them.
/// </remarks>
public static class RuntimeDirectory
{
    /// <summary>The environment variable that overrides the runtime folder.</summary>
    public const string EnvironmentVariable = "GLYPHLENS_RUNTIME_DIR";

    /// <summary>Resolves the runtime folder.</summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable (injected for tests).</param>
    public static RuntimeLocation Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } overridePath)
        {
            return LocalPaths.IsLocalAbsolute(overridePath)
                ? new RuntimeLocation(Path.GetFullPath(overridePath), null)
                : new RuntimeLocation(null, $"{EnvironmentVariable} is not an absolute local folder, so the setup will not use it. Change or remove it.");
        }

        return getEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } localAppData && LocalPaths.IsLocalAbsolute(localAppData)
            ? new RuntimeLocation(Path.Combine(localAppData, "GlyphLens", "runtime"), null)
            : new RuntimeLocation(null, "No runtime folder could be determined (LOCALAPPDATA is not set).");
    }

    /// <summary><c>&lt;runtime&gt;\tools</c>: uv's executable.</summary>
    public static string ToolsFolder(string root) => Path.Combine(Require(root), "tools");

    /// <summary><c>&lt;runtime&gt;\tools\uv.exe</c>.</summary>
    public static string UvExecutable(string root) => Path.Combine(ToolsFolder(root), SetupManifest.UvExecutableName);

    /// <summary><c>&lt;runtime&gt;\python</c>: the Python build uv installs.</summary>
    public static string PythonInstallFolder(string root) => Path.Combine(Require(root), "python");

    /// <summary><c>&lt;runtime&gt;\uv-cache</c>: uv's download cache (emptied after a successful install).</summary>
    public static string CacheFolder(string root) => Path.Combine(Require(root), "uv-cache");

    /// <summary><c>&lt;runtime&gt;\venv</c>: the virtual environment the worker runs in.</summary>
    public static string VirtualEnvironmentFolder(string root) => Path.Combine(Require(root), "venv");

    /// <summary><c>&lt;runtime&gt;\venv\Scripts\python.exe</c>: the interpreter the application prefers once it exists.</summary>
    public static string VirtualEnvironmentPython(string root) => Path.Combine(VirtualEnvironmentFolder(root), "Scripts", "python.exe");

    /// <summary><c>&lt;runtime&gt;\downloads</c>: files being downloaded (<c>.part</c>) and archives waiting to be imported.</summary>
    public static string DownloadsFolder(string root) => Path.Combine(Require(root), "downloads");

    /// <summary><c>&lt;runtime&gt;\setup.log</c>: what the last setup runs did (commands and installer output; never screen content).</summary>
    public static string LogFile(string root) => Path.Combine(Require(root), "setup.log");

    /// <summary><c>&lt;runtime&gt;\engines.lock.txt</c>: the hash-locked package list given to uv.</summary>
    public static string LockFile(string root) => Path.Combine(Require(root), "engines.lock.txt");

    /// <summary><c>&lt;runtime&gt;\venv\glyphlens-engines.sha256</c>: written after the packages were installed and checked.</summary>
    public static string LockMarker(string root) => Path.Combine(VirtualEnvironmentFolder(root), "glyphlens-engines.sha256");

    private static string Require(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return root;
    }
}
