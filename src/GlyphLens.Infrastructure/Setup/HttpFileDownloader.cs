using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Setup;

namespace GlyphLens.Infrastructure.Setup;

/// <summary>
/// Downloads the setup's pinned files. HTTPS only, also after redirects (which are followed by this class, at most five, never to plain
/// HTTP); the size and SHA-256 are enforced while the bytes arrive, so a file larger than pinned is abandoned at once and
/// <c>Content-Length</c> is never trusted on its own. Bytes go to <c>&lt;destination&gt;.part</c>, which is moved into place only after
/// it verified and is deleted on any failure or cancellation. The request carries only a <c>GlyphLens/&lt;version&gt;</c> User-Agent:
/// no cookies, no identifiers. The system proxy is used.
/// </summary>
public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    /// <summary>The suffix of a file that is still being downloaded.</summary>
    public const string PartialSuffix = ".part";

    private const int MaxRedirects = 5;
    private const int BufferSize = 81920;

    private static readonly TimeSpan DefaultHeadersTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly TimeSpan _headersTimeout;
    private readonly TimeSpan _stallTimeout;

    /// <param name="productVersion">The application version, sent as <c>GlyphLens/&lt;version&gt;</c>.</param>
    /// <param name="handler">The HTTP handler (tests inject one; the default uses the system proxy, no cookies and no automatic redirects).</param>
    /// <param name="headersTimeout">How long to wait for the response headers.</param>
    /// <param name="stallTimeout">How long a download may go without receiving any byte.</param>
    public HttpFileDownloader(string productVersion, HttpMessageHandler? handler = null, TimeSpan? headersTimeout = null, TimeSpan? stallTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false, // followed below, so every hop is checked for HTTPS
                AutomaticDecompression = DecompressionMethods.None, // the pinned bytes are the bytes on the wire
                ConnectTimeout = TimeSpan.FromSeconds(20),
                UseProxy = true,
            })
            : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per phase below: headers, then "no byte for too long"
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlyphLens", productVersion));
        _headersTimeout = headersTimeout ?? DefaultHeadersTimeout;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
    }

    public async Task<bool> DownloadAsync(PinnedFile file, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        RequireHttps(file.Url);

        if (await VerifiesAsync(destination, file, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(file.Size);
            return false;
        }

        string partial = destination + PartialSuffix;
        bool complete = false;
        try
        {
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var response = await SendAsync(file.Url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new DownloadException(DownloadFailureKind.Network, $"The server answered {(int)response.StatusCode} for {file.Url.Host}.");
            }

            if (response.Content.Headers.ContentLength is { } announced && announced != file.Size)
            {
                throw new DownloadException(DownloadFailureKind.SizeMismatch, SizeMessage(file));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                byte[] buffer = new byte[BufferSize];
                long total = 0;
                while (true)
                {
                    stall.CancelAfter(_stallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new DownloadException(DownloadFailureKind.Network, $"The download from {file.Url.Host} stopped (no data for {_stallTimeout.TotalSeconds:0} s).");
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > file.Size)
                    {
                        throw new DownloadException(DownloadFailureKind.SizeMismatch, SizeMessage(file));
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }

                if (total != file.Size)
                {
                    throw new DownloadException(DownloadFailureKind.SizeMismatch, $"The download of {file.DisplayName} ended early ({total:N0} of {file.Size:N0} bytes).");
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
            {
                throw new DownloadException(DownloadFailureKind.HashMismatch, HashMessage(file));
            }

            File.Move(partial, destination, overwrite: true);
            complete = true;
            return true;
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadFailureKind.Network, $"Could not download from {file.Url.Host}. Check the internet connection. ({ex.HttpRequestError})", ex);
        }
        catch (IOException ex)
        {
            throw new DownloadException(DownloadFailureKind.Storage, $"Could not write the downloaded file ({ex.GetType().Name}).", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DownloadException(DownloadFailureKind.Storage, "Could not write the downloaded file (access denied).", ex);
        }
        finally
        {
            if (!complete)
            {
                TryDelete(partial);
            }
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Whether <paramref name="path"/> exists with exactly the pinned size and SHA-256.</summary>
    public static async Task<bool> VerifiesAsync(string path, PinnedFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size || info.LinkTarget is not null)
            {
                return false;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(digest), file.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The message for a file whose bytes are not the pinned ones.</summary>
    public static string HashMessage(PinnedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"The file on the server changed: {file.DisplayName} from {file.Url.Host} does not have the expected SHA-256. It was deleted; do not use it.";
    }

    private static string SizeMessage(PinnedFile file) =>
        $"The file on the server changed: {file.DisplayName} from {file.Url.Host} does not have the expected size ({file.Size:N0} bytes). It was deleted; do not use it.";

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (int hop = 0; ; hop++)
        {
            RequireHttps(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headers.CancelAfter(_headersTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadException(DownloadFailureKind.Network, $"{current.Host} did not answer in time. Check the internet connection.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            using (response)
            {
                if (hop >= MaxRedirects || response.Headers.Location is not { } location)
                {
                    throw new DownloadException(DownloadFailureKind.Network, $"{url.Host} redirected too often or without a target.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new DownloadException(DownloadFailureKind.Insecure, $"{url.Host} redirected to an address that is not HTTPS; the download was refused.");
                }
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void RequireHttps(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new DownloadException(DownloadFailureKind.Insecure, "Only HTTPS downloads are allowed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next run, which removes stale partial files before it starts.
        }
    }
}
