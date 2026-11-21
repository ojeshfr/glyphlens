using GlyphLens.Core.Abstractions;
using GlyphLens.Windows.Platform;

namespace GlyphLens.Windows.Tests.Platform;

/// <summary>Real <c>.lnk</c> files written and read back with the Windows Shell, in a temporary folder (never the real desktop).</summary>
public sealed class ShellLinkShortcutFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glyphlens-lnk-" + Guid.NewGuid().ToString("N"));

    public ShellLinkShortcutFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AShortcut_IsWritten_AndReadsBackWithItsTargetFolderDescriptionAndIcon()
    {
        string target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string shortcut = Path.Combine(_folder, "GlyphLens.lnk");
        var files = new ShellLinkShortcutFiles();

        files.Create(shortcut, new ShortcutDefinition(target, Environment.SystemDirectory, "GlyphLens - offline screen translator", target, 0));

        Assert.True(File.Exists(shortcut));
        var read = files.Read(shortcut);
        Assert.Equal(target, read.TargetPath, ignoreCase: true);
        Assert.Equal(Environment.SystemDirectory, read.WorkingDirectory, ignoreCase: true);
        Assert.Equal("GlyphLens - offline screen translator", read.Description);
        Assert.Equal(target, read.IconPath, ignoreCase: true);
        Assert.Equal(0, read.IconIndex);
    }

    [Fact]
    public void AFolderWithSpacesAndNonAsciiCharacters_Works()
    {
        string folder = Path.Combine(_folder, "My Apps 翻译");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "GlyphLens.exe");
        File.WriteAllBytes(target, [0x4D, 0x5A]);
        string shortcut = Path.Combine(_folder, "GlyphLens.lnk");
        var files = new ShellLinkShortcutFiles();

        files.Create(shortcut, new ShortcutDefinition(target, folder, "GlyphLens - offline screen translator", target));

        var read = files.Read(shortcut);
        Assert.Equal(target, read.TargetPath, ignoreCase: true);
        Assert.Equal(folder, read.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void ReadingAMissingShortcut_IsAnIOException() =>
        Assert.ThrowsAny<IOException>(() => new ShellLinkShortcutFiles().Read(Path.Combine(_folder, "missing.lnk")));

    [Fact]
    public void WritingIntoAMissingFolder_IsAnIOException() =>
        Assert.ThrowsAny<IOException>(() => new ShellLinkShortcutFiles().Create(
            Path.Combine(_folder, "no", "such", "folder", "x.lnk"),
            new ShortcutDefinition(@"C:\x.exe", @"C:\", "x", @"C:\x.exe")));
}
