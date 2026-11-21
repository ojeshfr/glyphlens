using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlyphLens.App.Services;
using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.App.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly Uri Feed = new("https://updates.example/stable.json");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-upd-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _installer = RandomNumberGenerator.GetBytes(4096);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeClient : IUpdateClient
    {
        public byte[] FeedBytes { get; set; } = [];

        public byte[] Installer { get; set; } = [];

        public Exception? FeedFailure { get; set; }

        public int FeedRequests { get; private set; }

        public int Downloads { get; private set; }

        public Task<byte[]> GetFeedAsync(Uri feedUrl, CancellationToken cancellationToken)
        {
            FeedRequests++;
            return FeedFailure is null ? Task.FromResult(FeedBytes) : Task.FromException<byte[]>(FeedFailure);
        }

        public Task DownloadAsync(Uri url, string destination, long expectedSize, string expectedSha256, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, Installer);
            return Task.CompletedTask;
        }
    }

    private byte[] SignedFeed(string version)
    {
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version,
            url = $"https://updates.example/GlyphLens-{version}-setup.exe",
            sha256 = Convert.ToHexStringLower(SHA256.HashData(_installer)),
            size = (long)_installer.Length,
            published = "2026-12-01T00:00:00Z",
            notes = "Better Traditional Chinese.",
        });
        byte[] signature = _key.SignData(manifest, HashAlgorithmName.SHA256);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { manifest = Convert.ToBase64String(manifest), signature = Convert.ToBase64String(signature) }));
    }

    private (UpdateService Service, FakeClient Client, AppPreferencesStore Store) Create(bool configured = true, string current = "0.1.0")
    {
        var client = new FakeClient { Installer = _installer };
        var store = new AppPreferencesStore(Path.Combine(_folder, "preferences.json"));
        var configuration = configured ? new UpdateConfiguration(Feed, _key.ExportSubjectPublicKeyInfoPem()) : UpdateConfiguration.NotConfigured;
        return (new UpdateService(configuration, client, store, Version.Parse(current), Path.Combine(_folder, "updates")), client, store);
    }

    [Fact]
    public async Task WithoutConfiguration_NothingIsEverRequested()
    {
        var (service, client, _) = Create(configured: false);
        using (service)
        {
            service.Start();
            await service.CheckAsync();

            Assert.Equal(UpdateState.NotConfigured, service.State);
            Assert.Equal(0, client.FeedRequests);
        }
    }

    [Fact]
    public async Task ANewerSignedRelease_IsOffered_AndTheCheckIsRemembered()
    {
        var (service, client, store) = Create();
        using (service)
        {
            client.FeedBytes = SignedFeed("0.2.0");

            await service.CheckAsync();

            Assert.Equal(UpdateState.Available, service.State);
            Assert.Equal(new Version(0, 2, 0), service.Available!.Version);
            Assert.NotNull(store.Load().LastUpdateCheck);
            Assert.Equal(0, client.Downloads); // a check never downloads by itself
        }
    }

    [Fact]
    public async Task TheSameVersion_IsUpToDate()
    {
        var (service, client, _) = Create(current: "0.2.0");
        using (service)
        {
            client.FeedBytes = SignedFeed("0.2.0");

            await service.CheckAsync();

            Assert.Equal(UpdateState.UpToDate, service.State);
            Assert.Null(service.Available);
        }
    }

    [Fact]
    public async Task AnUnreachableServer_IsAFailureWithAPlainReason()
    {
        var (service, client, _) = Create();
        using (service)
        {
            client.FeedFailure = new HttpRequestException("no route");

            await service.CheckAsync();

            Assert.Equal(UpdateState.Failed, service.State);
            Assert.Contains("could not be reached", service.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DownloadThenInstall_HandsTheVerifiedInstallerOver()
    {
        var (service, client, _) = Create();
        using (service)
        {
            client.FeedBytes = SignedFeed("0.2.0");
            await service.CheckAsync();

            await service.DownloadAsync();
            string? launched = null;
            bool handedOver = service.Install(path => launched = path);

            Assert.True(handedOver);
            Assert.Equal(service.Installer, launched);
            Assert.EndsWith("GlyphLens-0.2.0-setup.exe", launched, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnInstallerChangedOnDiskAfterTheDownload_IsNotRun()
    {
        var (service, client, _) = Create();
        using (service)
        {
            client.FeedBytes = SignedFeed("0.2.0");
            await service.CheckAsync();
            await service.DownloadAsync();
            File.WriteAllBytes(service.Installer!, RandomNumberGenerator.GetBytes(_installer.Length));

            bool handedOver = service.Install(_ => Assert.Fail("a changed installer must not be run"));

            Assert.False(handedOver);
            Assert.Equal(UpdateState.Available, service.State);
            Assert.Contains("changed on disk", service.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TurningAutomaticChecksOff_IsRemembered()
    {
        var (service, _, store) = Create();
        using (service)
        {
            Assert.True(service.AutomaticChecks);

            service.AutomaticChecks = false;

            Assert.False(store.Load().CheckForUpdatesAutomatically);
        }
    }

    [Fact]
    public async Task TheSettingsSection_DescribesEachState()
    {
        var (service, client, _) = Create();
        using (service)
        using (var updates = new UpdatesViewModel(service, _ => { }, uiContext: null))
        {
            Assert.Contains("about once a day", updates.StatusText, StringComparison.Ordinal);
            Assert.False(updates.IsUpdateAvailable);

            client.FeedBytes = SignedFeed("0.2.0");
            await service.CheckAsync();

            Assert.True(updates.IsUpdateAvailable);
            Assert.True(updates.CanDownload);
            Assert.False(updates.CanInstall);
            Assert.Equal("GlyphLens 0.2.0 is available.", updates.BannerText);
            Assert.Equal("Better Traditional Chinese.", updates.Notes);

            await service.DownloadAsync();

            Assert.True(updates.CanInstall);
            Assert.Contains("downloaded and verified", updates.StatusText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBuiltInConfiguration_IsReadable_AndWithoutAKeyMeansNotConfigured()
    {
        var configuration = UpdateConfiguration.LoadEmbedded(typeof(UpdateService).Assembly);

        // The repository ships without a signing key; the release process adds one (docs/UPDATES.md).
        Assert.Equal(!string.IsNullOrWhiteSpace(configuration.PublicKeyPem) && configuration.FeedUrl is not null, configuration.IsConfigured);
    }

    [Theory]
    [InlineData("{\"feedUrl\":\"http://insecure.example/feed.json\",\"publicKeyPem\":\"KEY\"}", false)]
    [InlineData("{\"feedUrl\":\"https://ok.example/feed.json\",\"publicKeyPem\":\"\"}", false)]
    [InlineData("{\"feedUrl\":\"https://ok.example/feed.json\",\"publicKeyPem\":\"KEY\"}", true)]
    public void AConfigurationNeedsAnHttpsFeedAndAKey(string json, bool configured)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Equal(configured, UpdateConfiguration.Parse(stream).IsConfigured);
    }
}
