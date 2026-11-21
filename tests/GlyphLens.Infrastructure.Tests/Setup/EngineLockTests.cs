using System.Text.RegularExpressions;
using GlyphLens.Infrastructure.Setup;

namespace GlyphLens.Infrastructure.Tests.Setup;

public sealed partial class EngineLockTests
{
    private static readonly Lazy<EngineLock> Embedded = new(EngineLock.LoadEmbedded);

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GlyphLens.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }

    private static string Normalise(string name) => NameSeparators().Replace(name, "-").ToLowerInvariant();

    [Fact]
    public void TheEmbeddedLock_IsTheFileInTheRepository()
    {
        string onDisk = File.ReadAllText(RepositoryFile("worker", "requirements", "engines.lock.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(onDisk, Embedded.Value.Text);
        Assert.Matches("^[0-9a-f]{64}$", Embedded.Value.Sha256);
    }

    [Fact]
    public void EveryRequirement_IsPinnedWithEquals_AndHasAtLeastOneSha256()
    {
        var packages = Embedded.Value.Packages;

        Assert.Equal(70, packages.Count);
        Assert.All(packages, p =>
        {
            Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._-]*$", p.Name);
            Assert.Matches("^[0-9][0-9A-Za-z.+!-]*$", p.Version);
            Assert.NotEmpty(p.Sha256Hashes);
            Assert.All(p.Sha256Hashes, h => Assert.Matches("^[0-9a-f]{64}$", h));
        });
        Assert.Equal(packages.Count, packages.Select(p => Normalise(p.Name)).Distinct(StringComparer.Ordinal).Count());

        // The raw text agrees: every requirement line has "==" and is followed by hashes.
        foreach (string line in Embedded.Value.Text.Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith('#')))
        {
            Assert.Contains("==", line, StringComparison.Ordinal);
            Assert.EndsWith("\\", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDirectPins_AreTheTestedVersions_AndLanguageIdentificationAndTheWorkerAreNotIncluded()
    {
        var versions = Embedded.Value.Packages.ToDictionary(p => Normalise(p.Name), p => p.Version, StringComparer.Ordinal);

        Assert.Equal("4.9.0", versions["ctranslate2"]);
        Assert.Equal("0.2.3", versions["sentencepiece"]);
        Assert.Equal("3.7.0", versions["paddleocr"]);
        Assert.Equal("1.32.0", versions["onnxruntime"]);
        Assert.DoesNotContain("lingua-language-detector", versions.Keys);
        Assert.DoesNotContain("py3langid", versions.Keys);
        Assert.DoesNotContain("glyphlens-worker", versions.Keys);
        Assert.DoesNotContain("argostranslate", versions.Keys);
        Assert.DoesNotContain("paddlepaddle", versions.Keys);
        Assert.DoesNotContain("torch", versions.Keys);
    }

    [Fact]
    public void EveryLockedVersion_IsTheVersionOfTheTestedEnvironment()
    {
        var tested = File.ReadAllLines(RepositoryFile("worker", "requirements", "engines-constraints.txt"))
            .Where(l => l.Contains("==", StringComparison.Ordinal) && !l.StartsWith('#'))
            .Select(l => l.Split("==", 2))
            .ToDictionary(p => Normalise(p[0].Trim()), p => p[1].Trim(), StringComparer.Ordinal);

        Assert.All(Embedded.Value.Packages, p => Assert.Equal(tested[Normalise(p.Name)], p.Version));
    }

    [Fact]
    public void TheInputFile_PinsExactlyTheFourEnginePackages()
    {
        var pins = File.ReadAllLines(RepositoryFile("worker", "requirements", "engines.in"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.Equal(["ctranslate2==4.9.0", "sentencepiece==0.2.3", "paddleocr==3.7.0", "onnxruntime==1.32.0"], pins);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndLineEndings_AndTheHashDoesNotDependOnThem()
    {
        string lf = "# header\nabc==1.0 \\\n    --hash=sha256:" + new string('a', 64) + " \\\n    --hash=sha256:" + new string('b', 64) + "\n    # via x\n";
        var a = EngineLock.Parse(lf);
        var b = EngineLock.Parse(lf.Replace("\n", "\r\n", StringComparison.Ordinal));

        var package = Assert.Single(a.Packages);
        Assert.Equal("abc", package.Name);
        Assert.Equal("1.0", package.Version);
        Assert.Equal(2, package.Sha256Hashes.Count);
        Assert.Equal(a.Sha256, b.Sha256);
    }

    [Theory]
    [InlineData("abc>=1.0 \\\n    --hash=sha256:AAAA")]
    [InlineData("abc \\\n    --hash=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("abc==1.0\n")]
    [InlineData("abc==1.0 \\\n    --hash=sha256:xyz")]
    [InlineData("abc==1.0 \\\n    --hash=md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("abc==1.0; sys_platform == 'win32' \\\n    --hash=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("abc[extra]==1.0 \\\n    --hash=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("-e git+https://example/x.git#egg=x")]
    [InlineData("--index-url https://evil.example/simple")]
    [InlineData("# only a comment")]
    public void Parse_RefusesAnythingThatIsNotAnExactlyPinnedHashedRequirement(string text) =>
        Assert.Throws<FormatException>(() => EngineLock.Parse(text));

    [GeneratedRegex("[-_.]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameSeparators();
}
