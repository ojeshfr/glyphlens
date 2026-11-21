using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlyphLens.Infrastructure.Setup;

/// <summary>One locked package: its name, exact version and the SHA-256 hashes its files may have.</summary>
public sealed record LockedPackage(string Name, string Version, IReadOnlyList<string> Sha256Hashes);

/// <summary>
/// The hash-locked list of the worker's engine packages (OCR and translation) for Windows x64 and Python 3.12, generated from the
/// tested environment with <c>uv pip compile --generate-hashes</c> (see docs/DEVELOPMENT.md) and embedded in this assembly. uv installs
/// it with <c>--require-hashes --no-deps</c>, so every file must match one of the listed hashes and nothing else is resolved.
/// </summary>
public sealed class EngineLock
{
    /// <summary>The manifest resource name of the embedded lock file.</summary>
    public const string ResourceName = "GlyphLens.Setup.engines.lock.txt";

    private EngineLock(string text, IReadOnlyList<LockedPackage> packages)
    {
        Text = text;
        Packages = packages;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The file's text, exactly as embedded.</summary>
    public string Text { get; }

    /// <summary>The SHA-256 of <see cref="Text"/> (UTF-8): identifies this lock in the installed environment's marker file.</summary>
    public string Sha256 { get; }

    /// <summary>The packages, in file order.</summary>
    public IReadOnlyList<LockedPackage> Packages { get; }

    /// <summary>Reads the lock embedded in this assembly.</summary>
    public static EngineLock LoadEmbedded()
    {
        using var stream = typeof(EngineLock).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The engine lock file is not embedded in this build.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses a requirements file in the hash-locked form uv and pip write (<c>name==version \</c> then <c>--hash=sha256:...</c> lines).</summary>
    /// <exception cref="FormatException">A requirement is not pinned with <c>==</c>, has no hash, or a line is not understood.</exception>
    public static EngineLock Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal); // the same hash whatever line endings a checkout uses
        var packages = new List<LockedPackage>();
        string? name = null;
        string? version = null;
        var hashes = new List<string>();

        void Finish()
        {
            if (name is null)
            {
                return;
            }

            if (hashes.Count == 0)
            {
                throw new FormatException($"The requirement '{name}' has no --hash=sha256 line.");
            }

            packages.Add(new LockedPackage(name, version!, [.. hashes]));
            name = null;
            version = null;
            hashes.Clear();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                Finish();
                string requirement = trimmed.TrimEnd('\\').Trim();
                int separator = requirement.IndexOf("==", StringComparison.Ordinal);
                if (separator <= 0 || requirement.IndexOfAny([';', '<', '>', '!', '~', '@', ' ', '[']) >= 0 || requirement.IndexOf("==", separator + 2, StringComparison.Ordinal) >= 0)
                {
                    throw new FormatException($"The requirement '{requirement}' is not pinned with a single '=='.");
                }

                name = requirement[..separator];
                version = requirement[(separator + 2)..];
                if (version.Length == 0)
                {
                    throw new FormatException($"The requirement '{requirement}' has no version.");
                }

                continue;
            }

            const string prefix = "--hash=sha256:";
            if (name is null || !trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new FormatException("An unexpected line was found in the lock file.");
            }

            string hash = trimmed[prefix.Length..].TrimEnd('\\').Trim();
            if (hash.Length != 64 || !hash.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)))
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"The requirement '{name}' has a malformed hash."));
            }

            hashes.Add(hash);
        }

        Finish();
        if (packages.Count == 0)
        {
            throw new FormatException("The lock file lists no packages.");
        }

        return new EngineLock(text, packages);
    }
}
