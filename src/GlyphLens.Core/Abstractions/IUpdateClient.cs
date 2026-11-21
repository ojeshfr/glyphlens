namespace GlyphLens.Core.Abstractions;

/// <summary>The network side of the updater: fetch the small signed feed, and download an installer.</summary>
public interface IUpdateClient
{
    /// <summary>Downloads the update feed (at most <see cref="Updates.UpdateFeedReader.MaxFeedBytes"/>).</summary>
    Task<byte[]> GetFeedAsync(Uri feedUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. The file appears there only if it has exactly
    /// <paramref name="expectedSize"/> bytes and the SHA-256 <paramref name="expectedSha256"/>; otherwise nothing is left behind and
    /// an <see cref="InvalidDataException"/> is thrown.
    /// </summary>
    Task DownloadAsync(Uri url, string destination, long expectedSize, string expectedSha256, IProgress<double>? progress, CancellationToken cancellationToken);
}
