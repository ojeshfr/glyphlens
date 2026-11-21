using System.IO.Compression;
using System.Security.Cryptography;
using GlyphLens.Infrastructure.Setup;

namespace GlyphLens.Infrastructure.Tests.Setup;

public sealed class UvArchiveTests : IDisposable
{
    private static readonly byte[] Exe = RandomNumberGenerator.GetBytes(50_000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-uvzip-" + Guid.NewGuid().ToString("N"));

    public UvArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Zip(params (string Name, byte[] Content)[] entries)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }

    private string Target => Path.Combine(_folder, "tools", "uv.exe");

    private void Extract(string zip, long? size = null, string? sha = null) =>
        UvArchive.ExtractVerified(zip, "uv.exe", Target, size ?? Exe.Length, sha ?? Sha(Exe));

    [Fact]
    public void TheReleaseLayout_YieldsOnlyUvExe_Verified()
    {
        string zip = Zip(("uv.exe", Exe), ("uvw.exe", [1, 2]), ("uvx.exe", [3]));

        Extract(zip);

        Assert.Equal(Exe, File.ReadAllBytes(Target));
        Assert.Equal(["uv.exe"], Directory.GetFiles(Path.GetDirectoryName(Target)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("../uv.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("tools/uv.exe")]
    [InlineData("C:/Windows/uv.exe")]
    [InlineData("/abs.exe")]
    [InlineData("readme.txt")]
    [InlineData("uv.exe:stream")]
    public void AnUnexpectedEntry_RefusesTheWholeArchive(string name)
    {
        string zip = Zip(("uv.exe", Exe), (name, [9]));

        Assert.Throws<InvalidDataException>(() => Extract(zip));

        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Target)!).Any());
        Assert.False(File.Exists(Path.Combine(_folder, "evil.exe")));
    }

    [Fact]
    public void ADuplicateEntry_IsRefused()
    {
        string zip = Zip(("uv.exe", Exe), ("uv.exe", Exe));

        Assert.Throws<InvalidDataException>(() => Extract(zip));
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public void AWrongHash_IsRefused_AndNothingIsLeft()
    {
        byte[] other = (byte[])Exe.Clone();
        other[10] ^= 1;
        string zip = Zip(("uv.exe", other));

        var ex = Assert.Throws<InvalidDataException>(() => Extract(zip));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + HttpFileDownloader.PartialSuffix));
    }

    [Fact]
    public void AWrongSize_IsRefused()
    {
        string zip = Zip(("uv.exe", Exe));

        Assert.Throws<InvalidDataException>(() => Extract(zip, size: Exe.Length + 1));
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public void AnArchiveWithoutUvExe_IsRefused()
    {
        string zip = Zip(("uvx.exe", Exe));

        Assert.Throws<InvalidDataException>(() => Extract(zip));
    }

    [Fact]
    public void AnExistingUvExe_IsReplacedOnlyByAVerifiedOne()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllBytes(Target, [1, 2, 3]);

        Extract(Zip(("uv.exe", Exe)));

        Assert.Equal(Exe, File.ReadAllBytes(Target));
    }
}
