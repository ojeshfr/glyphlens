using System.Net;
using System.Security.Cryptography;
using GlyphLens.Core.Setup;
using GlyphLens.Infrastructure.Setup;

namespace GlyphLens.Infrastructure.Tests.Setup;

public sealed class HttpFileDownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-download-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Destination => Path.Combine(_folder, "sub", "file.bin");

    private static PinnedFile Pin(byte[] content, string url = "https://files.example/model.bin") =>
        new("test", "test file", new Uri(url), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), "MIT");

    /// <summary>Answers each request with what <c>respond</c> returns, and records the requests.</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Ok(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private void AssertNothingLeft()
    {
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + HttpFileDownloader.PartialSuffix));
    }

    [Fact]
    public async Task AFileThatMatchesThePin_IsKept_AndProgressIsReported()
    {
        byte[] content = RandomNumberGenerator.GetBytes(300_000);
        var handler = new Handler(_ => Ok(content));
        using var downloader = new HttpFileDownloader("1.2.3", handler);
        var reports = new List<long>();

        bool downloaded = await downloader.DownloadAsync(Pin(content), Destination, new SyncProgress(reports), CancellationToken.None);

        Assert.True(downloaded);
        Assert.Equal(content, File.ReadAllBytes(Destination));
        Assert.False(File.Exists(Destination + HttpFileDownloader.PartialSuffix));
        Assert.Equal(content.Length, reports[^1]);
        Assert.True(reports.Count > 1);
    }

    [Fact]
    public async Task TheRequest_CarriesOnlyAProductUserAgent_NoCookiesOrIdentifiers()
    {
        byte[] content = [1, 2, 3];
        var handler = new Handler(_ => Ok(content));
        using var downloader = new HttpFileDownloader("0.1.0", handler);

        await downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("GlyphLens/0.1.0", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
    }

    [Fact]
    public async Task AFileThatIsAlreadyThereAndVerifies_IsNotDownloadedAgain()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1000);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        File.WriteAllBytes(Destination, content);
        var handler = new Handler(_ => throw new InvalidOperationException("no request expected"));
        using var downloader = new HttpFileDownloader("1.0.0", handler);

        bool downloaded = await downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None);

        Assert.False(downloaded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFileThatIsThereButDiffers_IsReplacedByAVerifiedDownload()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1000);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        byte[] corrupted = (byte[])content.Clone();
        corrupted[500] ^= 0xFF;
        File.WriteAllBytes(Destination, corrupted);
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(content)));

        Assert.True(await downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));
        Assert.Equal(content, File.ReadAllBytes(Destination));
    }

    [Fact]
    public async Task AWrongHash_FailsWithTheServerChangedMessage_AndNothingIsKept()
    {
        byte[] content = RandomNumberGenerator.GetBytes(5000);
        byte[] served = (byte[])content.Clone();
        served[0] ^= 1; // one flipped bit
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(served)));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.HashMismatch, ex.Kind);
        Assert.Contains("The file on the server changed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("do not use it", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AnExistingFileWithTheWrongContent_IsNotLeftInPlaceWhenTheDownloadAlsoFails()
    {
        byte[] content = RandomNumberGenerator.GetBytes(100);
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(new byte[100])));

        await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        AssertNothingLeft();
    }

    [Fact]
    public async Task ABodyLargerThanPinned_IsAbandonedAsSoonAsItIsTooLarge_WithoutTrustingAMissingContentLength()
    {
        byte[] content = RandomNumberGenerator.GetBytes(10_000);
        var endless = new EndlessStream(); // no Content-Length, never ends
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(endless)));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.SizeMismatch, ex.Kind);
        Assert.True(endless.BytesRead < content.Length + 200_000, $"read {endless.BytesRead} bytes before stopping");
        AssertNothingLeft();
    }

    [Fact]
    public async Task AContentLengthThatDiffersFromThePin_IsRefusedBeforeReading()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1000);
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(new byte[999])));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.SizeMismatch, ex.Kind);
        AssertNothingLeft();
    }

    [Fact]
    public async Task ABodyThatEndsEarly_IsRefused()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1000);
        var response = Ok(new UnseekableStream(new MemoryStream(content, 0, 600))); // no length announced
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => response));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.SizeMismatch, ex.Kind);
        Assert.Contains("ended early", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task PlainHttp_IsRefusedBeforeAnyRequest()
    {
        byte[] content = [1];
        var handler = new Handler(_ => Ok(content));
        using var downloader = new HttpFileDownloader("1.0.0", handler);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content, "http://files.example/a"), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.Insecure, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARedirectFromHttpsToHttp_IsRefused()
    {
        byte[] content = [1, 2];
        var handler = new Handler(r => r.RequestUri!.Scheme == "https" ? Redirect("http://cdn.example/file") : Ok(content));
        using var downloader = new HttpFileDownloader("1.0.0", handler);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.Insecure, ex.Kind);
        Assert.Single(handler.Requests); // the http address was never requested
        AssertNothingLeft();
    }

    [Fact]
    public async Task HttpsRedirects_AreFollowed_IncludingRelativeOnes()
    {
        byte[] content = RandomNumberGenerator.GetBytes(64);
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/model.bin" => Redirect("/api/resolve-cache/model.bin", HttpStatusCode.TemporaryRedirect),
            "/api/resolve-cache/model.bin" => Redirect("https://cdn.example/x/model.bin"),
            _ => Ok(content),
        });
        using var downloader = new HttpFileDownloader("1.0.0", handler);

        Assert.True(await downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(
            ["https://files.example/model.bin", "https://files.example/api/resolve-cache/model.bin", "https://cdn.example/x/model.bin"],
            handler.Requests.Select(r => r.RequestUri!.ToString()));
    }

    [Fact]
    public async Task EndlessRedirects_Stop()
    {
        byte[] content = [1];
        var handler = new Handler(_ => Redirect("https://files.example/model.bin"));
        using var downloader = new HttpFileDownloader("1.0.0", handler);

        await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(6, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.PartialContent)]
    public async Task AnyAnswerButOk_IsAFailure(HttpStatusCode status)
    {
        byte[] content = [1];
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(content) }));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.Network, ex.Kind);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task ANetworkError_IsReportedInPlainWords()
    {
        byte[] content = [1];
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no dns")));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.Network, ex.Kind);
        Assert.Contains("internet connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_DeletesThePartialFile()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1_000_000);
        using var cts = new CancellationTokenSource();
        var slow = new GatedStream(content, afterBytes: 200_000, onGate: cts.Cancel);
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(slow)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Pin(content), Destination, null, cts.Token));

        AssertNothingLeft();
    }

    [Fact]
    public async Task ADownloadThatStalls_FailsAfterTheStallTimeout_AndLeavesNothing()
    {
        byte[] content = RandomNumberGenerator.GetBytes(100_000);
        var stalled = new GatedStream(content, afterBytes: 10_000, onGate: null);
        using var downloader = new HttpFileDownloader("1.0.0", new Handler(_ => Ok(stalled)), stallTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Pin(content), Destination, null, CancellationToken.None));

        Assert.Equal(DownloadFailureKind.Network, ex.Kind);
        Assert.Contains("stopped", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task VerifiesAsync_ChecksSizeAndHash()
    {
        byte[] content = RandomNumberGenerator.GetBytes(10);
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "x");
        Assert.False(await HttpFileDownloader.VerifiesAsync(path, Pin(content), CancellationToken.None));
        File.WriteAllBytes(path, content);
        Assert.True(await HttpFileDownloader.VerifiesAsync(path, Pin(content), CancellationToken.None));
        File.WriteAllBytes(path, [.. content, 0]);
        Assert.False(await HttpFileDownloader.VerifiesAsync(path, Pin(content), CancellationToken.None));
    }

    private sealed class SyncProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }

    /// <summary>A stream whose length cannot be known in advance (so no Content-Length is announced).</summary>
    private sealed class UnseekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A body without end and without a length.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)7, offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves bytes until <c>afterBytes</c>, then calls <c>onGate</c> and waits until the read is cancelled.</summary>
    private sealed class GatedStream(byte[] content, int afterBytes, Action? onGate) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= afterBytes)
            {
                onGate?.Invoke();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            int n = Math.Min(buffer.Length, Math.Min(afterBytes, content.Length) - _position);
            content.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
