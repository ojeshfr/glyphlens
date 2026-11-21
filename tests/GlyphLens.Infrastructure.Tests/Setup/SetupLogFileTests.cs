using GlyphLens.Infrastructure.Setup;

namespace GlyphLens.Infrastructure.Tests.Setup;

public sealed class SetupLogFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-setuplog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string LogPath => Path.Combine(_folder, "runtime", "setup.log");

    [Fact]
    public void NothingIsWritten_UntilALineIsLogged()
    {
        var log = new SetupLogFile(LogPath);

        Assert.Equal(LogPath, log.FilePath);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Lines_AreTimestamped_CleanedOfControlCharacters_AndShortened()
    {
        var log = new SetupLogFile(LogPath);

        log.Write("hello\u001b[31m red\tand\rmore");
        log.Write(new string('x', 2000));

        string[] lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}Z hello \\[31m red and more$", lines[0]);
        Assert.True(lines[1].Length < 700);
        Assert.EndsWith("…", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeOldLog_IsMovedAside_WhenANewRunStarts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('o', 1_100_000));

        new SetupLogFile(LogPath).Write("new run");

        Assert.True(File.Exists(Path.Combine(_folder, "runtime", "setup.previous.log")));
        Assert.Single(File.ReadAllLines(LogPath));
    }

    [Fact]
    public void AnUnwritableLog_NeverThrows()
    {
        Directory.CreateDirectory(LogPath); // a folder where the file should be

        var log = new SetupLogFile(LogPath);
        log.Write("ignored");
    }
}
