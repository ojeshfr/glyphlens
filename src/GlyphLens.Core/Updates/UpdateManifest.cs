using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GlyphLens.Core.Updates;

/// <summary>A release GlyphLens can update to, as described by the signed update feed.</summary>
/// <param name="Version">The release's version.</param>
/// <param name="DownloadUrl">Where the installer is (HTTPS only).</param>
/// <param name="Sha256">The installer's SHA-256, 64 lower-case hex digits.</param>
/// <param name="Size">The installer's size in bytes.</param>
/// <param name="Published">When the release was published.</param>
/// <param name="Notes">Short release notes (plain text).</param>
public sealed record UpdateManifest(Version Version, Uri DownloadUrl, string Sha256, long Size, DateTimeOffset Published, string Notes)
{
    /// <summary>The installer's file name (the last segment of <see cref="DownloadUrl"/>).</summary>
    public string FileName => Path.GetFileName(DownloadUrl.AbsolutePath);
}

/// <summary>The outcome of reading the update feed: a verified manifest, or why there is none.</summary>
public sealed record UpdateFeedResult(UpdateManifest? Manifest, string? Problem)
{
    public static UpdateFeedResult Fail(string problem) => new(null, problem);
}

/// <summary>
/// Reads and verifies the update feed. The feed is a small JSON document
/// <c>{ "manifest": base64(manifest JSON), "signature": base64(ECDSA P-256 / SHA-256 signature of those bytes) }</c>; the signature is
/// checked with the public key built into GlyphLens, so a manifest changed by anyone without the private signing key (for example
/// whoever controls the web host) is rejected before anything is downloaded.
/// </summary>
/// <remarks>The manifest JSON: <c>{ "version": "0.2.0", "url": "https://…/GlyphLens-0.2.0-setup.exe", "sha256": "…", "size": 12345,
/// "published": "2026-12-01T00:00:00Z", "notes": "…" }</c>.</remarks>
public static class UpdateFeedReader
{
    /// <summary>The largest feed document read (bytes).</summary>
    public const int MaxFeedBytes = 64 * 1024;

    /// <summary>The largest installer accepted (bytes).</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    private const int MaxNotesLength = 4000;

    public static UpdateFeedResult Read(ReadOnlySpan<byte> feed, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return UpdateFeedResult.Fail("This build has no update signing key, so no update can be verified.");
        }

        if (feed.Length == 0 || feed.Length > MaxFeedBytes)
        {
            return UpdateFeedResult.Fail("The update information has an unexpected size.");
        }

        byte[] manifestBytes;
        byte[] signature;
        try
        {
            using var document = JsonDocument.Parse(feed.ToArray());
            manifestBytes = Convert.FromBase64String(document.RootElement.GetProperty("manifest").GetString() ?? string.Empty);
            signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return UpdateFeedResult.Fail("The update information could not be read.");
        }

        if (!VerifySignature(manifestBytes, signature, publicKeyPem))
        {
            return UpdateFeedResult.Fail("The update information is not signed by the GlyphLens release key; it was ignored.");
        }

        return ParseManifest(manifestBytes);
    }

    /// <summary>Whether <paramref name="signature"/> is a valid ECDSA P-256 / SHA-256 signature of <paramref name="data"/>.</summary>
    public static bool VerifySignature(byte[] data, byte[] signature, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.KeySize == 256 && key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Validates the (already verified) manifest JSON.</summary>
    public static UpdateFeedResult ParseManifest(byte[] manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;
            string versionText = root.GetProperty("version").GetString() ?? string.Empty;
            string urlText = root.GetProperty("url").GetString() ?? string.Empty;
            string sha256 = (root.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant();
            long size = root.GetProperty("size").GetInt64();
            string publishedText = root.GetProperty("published").GetString() ?? string.Empty;
            string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? string.Empty : string.Empty;

            if (!Version.TryParse(versionText, out var version) || version.Major < 0)
            {
                return UpdateFeedResult.Fail("The update has no valid version number.");
            }

            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(url.Host))
            {
                return UpdateFeedResult.Fail("The update is not offered over HTTPS.");
            }

            string extension = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
            if (extension is not (".exe" or ".msi"))
            {
                return UpdateFeedResult.Fail("The update is not an installer (.exe or .msi).");
            }

            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                return UpdateFeedResult.Fail("The update has no valid SHA-256.");
            }

            if (size <= 0 || size > MaxPackageBytes)
            {
                return UpdateFeedResult.Fail("The update has an unexpected size.");
            }

            if (!DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
            {
                return UpdateFeedResult.Fail("The update has no valid publication date.");
            }

            notes = notes.Trim();
            if (notes.Length > MaxNotesLength)
            {
                notes = notes[..MaxNotesLength] + "…";
            }

            return new UpdateFeedResult(new UpdateManifest(version, url, sha256, size, published, notes), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return UpdateFeedResult.Fail("The update information is incomplete.");
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is newer than <paramref name="current"/> (missing parts count as 0).</summary>
    public static bool IsNewer(Version candidate, Version current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        return Normalize(candidate).CompareTo(Normalize(current)) > 0;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}

/// <summary>
/// When GlyphLens asks the update server: not before <see cref="FirstCheckDelay"/> after start-up (start-up stays quick), then about
/// once a day; after a failure, again after <see cref="RetryAfterFailure"/>.
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    public static DateTimeOffset NextCheckAt(DateTimeOffset startedAt, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure)
    {
        var earliest = startedAt + FirstCheckDelay;
        var due = lastSuccess is { } success ? success + Interval : earliest;
        if (lastFailure is { } failure && (lastSuccess is null || failure > lastSuccess))
        {
            var retry = failure + RetryAfterFailure;
            if (retry > due)
            {
                due = retry;
            }
        }

        return due > earliest ? due : earliest;
    }
}
