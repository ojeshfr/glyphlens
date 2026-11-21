using System.Net;
using System.Security.Cryptography;
using GlyphLens.Infrastructure.Updates;

namespace GlyphLens.Infrastructure.Tests.Updates;

public sealed class HttpUpdateClientTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

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

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task TheFeedRequest_SendsOnlyAProductUserAgent_NoCookiesOrIdentifiers()
    {
        var handler = new Handler(_ => Bytes("{}"u8.ToArray()));
        using var client = new HttpUpdateClient("0.1.0", handler);

        byte[] feed = await client.GetFeedAsync(new Uri("https://updates.example/stable.json"), CancellationToken.None);

        Assert.Equal("{}"u8.ToArray(), feed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("GlyphLens/0.1.0", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal(string.Empty, request.RequestUri!.Query);
    }

    [Fact]
    public async Task PlainHttp_IsRefused_BeforeAnyRequest()
    {
        var handler = new Handler(_ => Bytes([]));
        using var client = new HttpUpdateClient("0.1.0", handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetFeedAsync(new Uri("http://updates.example/stable.json"), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnOversizedFeed_IsRefused()
    {
        using var client = new HttpUpdateClient("0.1.0", new Handler(_ => Bytes(new byte[70 * 1024])));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetFeedAsync(new Uri("https://updates.example/stable.json"), CancellationToken.None));
    }

    [Fact]
    public async Task ADownloadMatchingTheManifest_IsKept()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(200_000);
        using var client = new HttpUpdateClient("0.1.0", new Handler(_ => Bytes(installer)));
        string destination = Path.Combine(_folder, "GlyphLens-0.2.0-setup.exe");
        var reports = new List<double>();

        await client.DownloadAsync(new Uri("https://updates.example/GlyphLens-0.2.0-setup.exe"), destination, installer.Length, Convert.ToHexStringLower(SHA256.HashData(installer)), new SyncProgress(reports), CancellationToken.None);

        Assert.Equal(installer, File.ReadAllBytes(destination));
        Assert.Equal(1.0, reports[^1], 3);
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ADownloadWithTheWrongHashOrSize_IsDeleted(bool wrongHash, bool wrongSize)
    {
        byte[] installer = RandomNumberGenerator.GetBytes(10_000);
        using var client = new HttpUpdateClient("0.1.0", new Handler(_ => Bytes(installer)));
        string destination = Path.Combine(_folder, "setup.exe");
        string sha = wrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(installer));
        long size = wrongSize ? installer.Length - 1 : installer.Length;

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(new Uri("https://updates.example/setup.exe"), destination, size, sha, null, CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
