using System.Net.Http.Headers;
using System.Security.Cryptography;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Updates;

namespace GlyphLens.Infrastructure.Updates;

/// <summary>
/// The updater's only network access: HTTPS GET of the small update feed and of an installer. It sends nothing about the user or
/// the screen: no cookies, no identifiers, only a product User-Agent ("GlyphLens/version"). Sizes are capped, the feed must be HTTPS,
/// and an installer is kept only if its size and SHA-256 match the signed manifest.
/// </summary>
public sealed class HttpUpdateClient : IUpdateClient, IDisposable
{
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    public HttpUpdateClient(string productVersion, HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
            : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per request (feed: 20 s; installers: as long as the bytes keep coming)
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlyphLens", productVersion));
    }

    public async Task<byte[]> GetFeedAsync(Uri feedUrl, CancellationToken cancellationToken)
    {
        RequireHttps(feedUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        using var response = await _http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        RequireHttps(response.RequestMessage?.RequestUri ?? feedUrl); // also after redirects
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > UpdateFeedReader.MaxFeedBytes)
        {
            throw new InvalidDataException("The update information is larger than expected.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > UpdateFeedReader.MaxFeedBytes)
            {
                throw new InvalidDataException("The update information is larger than expected.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public async Task DownloadAsync(Uri url, string destination, long expectedSize, string expectedSha256, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        RequireHttps(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSize);

        string partial = destination + ".partial";
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bool complete = false;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            RequireHttps(response.RequestMessage?.RequestUri ?? url);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } announced && announced != expectedSize)
            {
                throw new InvalidDataException("The download does not have the size the update information announced.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > expectedSize)
                    {
                        throw new InvalidDataException("The download is larger than the update information announced.");
                    }

                    hash.AppendData(chunk, 0, read);
                    await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)total / expectedSize);
                }

                if (total != expectedSize)
                {
                    throw new InvalidDataException("The download ended early.");
                }
            }

            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The download's SHA-256 does not match the signed update information; it was deleted.");
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

    public void Dispose() => _http.Dispose();

    private static void RequireHttps(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Updates are only fetched over HTTPS.");
        }
    }
}
