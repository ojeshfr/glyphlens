using System.Text.RegularExpressions;
using GlyphLens.Core.Ocr;
using GlyphLens.Core.Setup;

namespace GlyphLens.Core.Tests.Setup;

public sealed partial class SetupManifestTests
{
    public static TheoryData<string> FileIds()
    {
        var data = new TheoryData<string>();
        foreach (var file in SetupManifest.AllFiles)
        {
            data.Add(file.Id);
        }

        return data;
    }

    private static PinnedFile File(string id) => SetupManifest.AllFiles.Single(f => f.Id == id);

    [Theory]
    [MemberData(nameof(FileIds))]
    public void EveryPinnedFile_IsHttps_HasASize_AndA64HexSha256(string id)
    {
        var file = File(id);

        Assert.True(file.Url.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, file.Url.Scheme);
        Assert.True(file.Size > 0);
        Assert.Matches(Sha256(), file.Sha256);
        Assert.False(string.IsNullOrWhiteSpace(file.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(file.Licence));
        Assert.Empty(file.Url.Query);
    }

    [Fact]
    public void TheIdsAndHashesAreUnique_AndTheListIsComplete()
    {
        Assert.Equal(SetupManifest.AllFiles.Count, SetupManifest.AllFiles.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SetupManifest.AllFiles.Count, SetupManifest.AllFiles.Select(f => f.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, SetupManifest.AllFiles.Count); // uv, four OCR files, two translation models: nothing else is downloaded by GlyphLens' code
    }

    [Fact]
    public void TheFilesComeOnlyFromTheirOfficialPublishers()
    {
        string[] hosts = ["github.com", "huggingface.co", "argos-net.com"];

        Assert.All(SetupManifest.AllFiles, f => Assert.Contains(f.Host, hosts));
        Assert.Equal("github.com", SetupManifest.Uv.Host);
        Assert.StartsWith("/astral-sh/uv/releases/download/", SetupManifest.Uv.Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void UvIsPinnedToOneRelease_AndItsExecutableToASizeAndHash()
    {
        Assert.Matches("^\\d+\\.\\d+\\.\\d+$", SetupManifest.UvVersion);
        Assert.Contains($"/{SetupManifest.UvVersion}/uv-x86_64-pc-windows-msvc.zip", SetupManifest.Uv.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("uv.exe", SetupManifest.UvExecutableName);
        Assert.True(SetupManifest.UvExecutableSize > SetupManifest.Uv.Size); // the archive is compressed
        Assert.Matches(Sha256(), SetupManifest.UvExecutableSha256);
        Assert.Equal("0.13.7", SetupManifest.UvVersion);
        Assert.Equal("0cdc5b38fe5a8168ed6a25814f5ba43c21d313dd3187314fccb37b7cec46e021", SetupManifest.Uv.Sha256);
    }

    [Fact]
    public void TheOcrFiles_ArePinnedToACommit_AndGoWhereTheWorkerReadsThem()
    {
        Assert.Equal(4, SetupManifest.OcrFiles.Count);
        foreach (var ocr in SetupManifest.OcrFiles)
        {
            Assert.Matches("^/PaddlePaddle/PP-OCRv5_mobile_(det|rec)_onnx/resolve/[0-9a-f]{40}/inference\\.(onnx|yml)$", ocr.File.Url.AbsolutePath);
            Assert.Equal("Apache-2.0", ocr.File.Licence);
            string[] parts = ocr.RelativePath.Split(Path.DirectorySeparatorChar);
            Assert.Equal(3, parts.Length);
            Assert.Equal(ModelsDirectory.OcrSubdirectory, parts[0]);
            Assert.Contains(parts[1], ModelsDirectory.OcrModelFolders);
            Assert.Equal(Path.GetFileName(ocr.File.Url.AbsolutePath), parts[2]);
            Assert.Contains(parts[1], ocr.File.Url.AbsolutePath, StringComparison.Ordinal);
        }

        // Each folder gets both of its files.
        foreach (string folder in ModelsDirectory.OcrModelFolders)
        {
            Assert.Equal(
                ["inference.onnx", "inference.yml"],
                SetupManifest.OcrFiles.Where(f => f.RelativePath.Contains(folder, StringComparison.Ordinal)).Select(f => Path.GetFileName(f.RelativePath)).Order(StringComparer.Ordinal));
        }

        Assert.Equal(4_826_518 + 903 + 16_534_782 + 148_345, SetupManifest.OcrBytes);
    }

    [Fact]
    public void TheTranslationModels_AreTheArgos19Packages()
    {
        Assert.Equal("https://argos-net.com/v1/translate-zh_en-1_9.argosmodel", SetupManifest.SimplifiedChineseModel.Url.ToString());
        Assert.Equal(74_481_402, SetupManifest.SimplifiedChineseModel.Size);
        Assert.Equal("62e7af5a3a48b530e47b7b3e5c78c2de79073ecd815750d2bf3ab35b4a67da2d", SetupManifest.SimplifiedChineseModel.Sha256);
        Assert.Equal("https://argos-net.com/v1/translate-zt_en-1_9.argosmodel", SetupManifest.TraditionalChineseModel.Url.ToString());
        Assert.Equal(74_498_034, SetupManifest.TraditionalChineseModel.Size);
        Assert.Equal("a9ea826d801f059dd47f1f47b6c1dc9d762519a80ea3afd2a982130a42a25486", SetupManifest.TraditionalChineseModel.Sha256);
        Assert.All(new[] { SetupManifest.SimplifiedChineseModel, SetupManifest.TraditionalChineseModel }, m => Assert.Equal("CC-BY 4.0", m.Licence));
        Assert.All(SetupManifest.AllFiles, f => Assert.True(f.Size < 1024L * 1024 * 1024, $"{f.Id} is larger than 1 GB"));
    }

    [Fact]
    public void ThePythonAndSpaceSettings_AreSane()
    {
        Assert.Equal(new Version(3, 12), SetupManifest.PythonVersion);
        Assert.StartsWith("cpython-3.12.", SetupManifest.PythonRequest, StringComparison.Ordinal);
        Assert.EndsWith("-windows-x86_64-none", SetupManifest.PythonRequest, StringComparison.Ordinal);
        Assert.Equal("https://pypi.org/simple", SetupManifest.PackageIndex.ToString());
        Assert.True(SetupManifest.RequiredFreeBytes >= 1_500L * 1000 * 1000);
        Assert.True(SetupManifest.RequiredFreeBytesForModels > SetupManifest.SimplifiedChineseModel.Size + SetupManifest.TraditionalChineseModel.Size + SetupManifest.OcrBytes);
        Assert.Contains(SetupManifest.Hosts, h => h.Host == "releases.astral.sh");
        Assert.Contains(SetupManifest.Hosts, h => h.Host.Contains("pypi.org", StringComparison.Ordinal));
        Assert.All(SetupManifest.AllFiles, f => Assert.Contains(SetupManifest.Hosts, h => h.Host.Contains(f.Host, StringComparison.Ordinal)));
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
