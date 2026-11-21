using System.IO.Compression;
using System.Security.Cryptography;

namespace GlyphLens.Infrastructure.Setup;

/// <summary>
/// Takes <c>uv.exe</c> out of the verified uv release archive. Only that one entry is extracted. The archive must contain exactly the
/// entries uv's Windows release has (<c>uv.exe</c>, <c>uvw.exe</c>, <c>uvx.exe</c>, all at the top level); anything else (a folder, a
/// path with <c>..</c>, an absolute or drive-qualified name, a duplicate, an unknown file) makes the whole archive refused. The extracted
/// bytes must have the pinned size and SHA-256; they are written to a <c>.part</c> file and moved into place only after that check.
/// </summary>
public static class UvArchive
{
    /// <summary>The entries uv's Windows x64 release archive contains.</summary>
    public static IReadOnlyList<string> ExpectedEntries { get; } = ["uv.exe", "uvw.exe", "uvx.exe"];

    /// <summary>Extracts <paramref name="entryName"/> from <paramref name="archivePath"/> to <paramref name="destination"/>.</summary>
    /// <exception cref="InvalidDataException">The archive has unexpected entries, or the entry does not have the expected size and hash.</exception>
    public static void ExtractVerified(string archivePath, string entryName, string destination, long expectedSize, string expectedSha256, IReadOnlyCollection<string>? allowedEntries = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSize);
        var allowed = new HashSet<string>(allowedEntries ?? ExpectedEntries, StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(archivePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? wanted = null;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            if (!IsPlainName(name) || !allowed.Contains(name))
            {
                throw new InvalidDataException("The uv archive contains an unexpected entry; it was not used.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidDataException("The uv archive contains a duplicate entry; it was not used.");
            }

            if (string.Equals(name, entryName, StringComparison.Ordinal))
            {
                wanted = entry;
            }
        }

        if (wanted is null)
        {
            throw new InvalidDataException($"The uv archive does not contain {entryName}.");
        }

        if (wanted.Length != expectedSize)
        {
            throw new InvalidDataException($"{entryName} in the uv archive does not have the expected size.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partial = destination + HttpFileDownloader.PartialSuffix;
        bool complete = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var source = wanted.Open())
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > expectedSize)
                    {
                        throw new InvalidDataException($"{entryName} in the uv archive is larger than expected.");
                    }

                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }

                if (total != expectedSize)
                {
                    throw new InvalidDataException($"{entryName} in the uv archive is smaller than expected.");
                }
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{entryName} in the uv archive does not have the expected SHA-256; it was not used.");
            }

            File.Move(partial, destination, overwrite: true);
            complete = true;
        }
        finally
        {
            if (!complete && File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>A top-level file name: no folder separators, no drive, no <c>..</c>, no control or reserved characters.</summary>
    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 64
        && name.IndexOfAny(['/', '\\', ':', '\0']) < 0
        && name != "."
        && name != ".."
        && !name.Any(char.IsControl)
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
}
