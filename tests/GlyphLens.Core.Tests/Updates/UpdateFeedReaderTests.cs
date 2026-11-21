using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlyphLens.Core.Updates;

namespace GlyphLens.Core.Tests.Updates;

public sealed class UpdateFeedReaderTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    private static string ManifestJson(string version = "0.2.0", string url = "https://glyphlens.example/GlyphLens-0.2.0-setup.exe", string? sha = null, long size = 1234, string notes = "Faster OCR.") =>
        JsonSerializer.Serialize(new { version, url, sha256 = sha ?? new string('a', 64), size, published = "2026-12-01T00:00:00Z", notes });

    private byte[] Feed(string manifest, ECDsa? signer = null, Func<byte[], byte[]>? tamper = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(manifest);
        byte[] signature = (signer ?? _key).SignData(bytes, HashAlgorithmName.SHA256);
        if (tamper is not null)
        {
            bytes = tamper(bytes);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new { manifest = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(signature) });
    }

    [Fact]
    public void ACorrectlySignedFeed_GivesTheRelease()
    {
        var result = UpdateFeedReader.Read(Feed(ManifestJson()), PublicKey);

        Assert.Null(result.Problem);
        Assert.Equal(new Version(0, 2, 0), result.Manifest!.Version);
        Assert.Equal("GlyphLens-0.2.0-setup.exe", result.Manifest.FileName);
        Assert.Equal(1234, result.Manifest.Size);
        Assert.Equal("Faster OCR.", result.Manifest.Notes);
    }

    [Fact]
    public void AFeedSignedWithAnotherKey_IsRejected()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = UpdateFeedReader.Read(Feed(ManifestJson(), stranger), PublicKey);

        Assert.Null(result.Manifest);
        Assert.Contains("not signed", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestChangedAfterSigning_IsRejected()
    {
        var result = UpdateFeedReader.Read(Feed(ManifestJson(), tamper: b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("0.2.0", "9.9.9", StringComparison.Ordinal))), PublicKey);

        Assert.Null(result.Manifest);
    }

    [Fact]
    public void WithoutAKey_NothingIsAccepted() =>
        Assert.Null(UpdateFeedReader.Read(Feed(ManifestJson()), string.Empty).Manifest);

    [Theory]
    [InlineData("http://glyphlens.example/GlyphLens-0.2.0-setup.exe", "HTTPS")]
    [InlineData("https://glyphlens.example/GlyphLens-0.2.0.zip", "installer")]
    [InlineData("file:///C:/GlyphLens-setup.exe", "HTTPS")]
    public void UnsafeDownloadLocations_AreRejected_EvenWhenSigned(string url, string expected)
    {
        var result = UpdateFeedReader.Read(Feed(ManifestJson(url: url)), PublicKey);

        Assert.Null(result.Manifest);
        Assert.Contains(expected, result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-version", 10, null)]
    [InlineData("1.0.0", 0, null)]
    [InlineData("1.0.0", 2_000_000_000L, null)]
    [InlineData("1.0.0", 10, "xyz")]
    public void InvalidFields_AreRejected(string version, long size, string? sha)
    {
        var result = UpdateFeedReader.Read(Feed(ManifestJson(version: version, size: size, sha: sha)), PublicKey);

        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    public void Garbage_IsRejectedWithoutThrowing(string feed) =>
        Assert.Null(UpdateFeedReader.Read(Encoding.UTF8.GetBytes(feed), PublicKey).Manifest);

    [Fact]
    public void AnOversizedFeed_IsRejected() =>
        Assert.Null(UpdateFeedReader.Read(new byte[UpdateFeedReader.MaxFeedBytes + 1], PublicKey).Manifest);

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1", "0.1.0", false)]
    [InlineData("0.1.0.1", "0.1.0", true)]
    [InlineData("0.0.9", "0.1.0", false)]
    public void VersionsCompareNumerically(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateFeedReader.IsNewer(Version.Parse(candidate), Version.Parse(current)));
}

public sealed class UpdateScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstCheck_WaitsAfterStartUp() =>
        Assert.Equal(Start + UpdateSchedule.FirstCheckDelay, UpdateSchedule.NextCheckAt(Start, null, null));

    [Fact]
    public void AfterASuccess_TheNextCheckIsADayLater()
    {
        var last = Start.AddHours(-2);

        Assert.Equal(last + UpdateSchedule.Interval, UpdateSchedule.NextCheckAt(Start, last, null));
    }

    [Fact]
    public void ACheckLongAgo_HappensSoonAfterStartUp_NotDuringIt() =>
        Assert.Equal(Start + UpdateSchedule.FirstCheckDelay, UpdateSchedule.NextCheckAt(Start, Start.AddDays(-3), null));

    [Fact]
    public void AfterAFailure_TheRetryWaits()
    {
        var failure = Start.AddMinutes(1);

        Assert.Equal(failure + UpdateSchedule.RetryAfterFailure, UpdateSchedule.NextCheckAt(Start, null, failure));
    }
}
