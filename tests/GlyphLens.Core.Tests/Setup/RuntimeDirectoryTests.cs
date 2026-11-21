using GlyphLens.Core.Setup;

namespace GlyphLens.Core.Tests.Setup;

public sealed class RuntimeDirectoryTests
{
    private const string LocalAppData = @"C:\Users\a\AppData\Local";

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void WithoutAnOverride_TheRuntimeIsUnderLocalAppData()
    {
        var location = RuntimeDirectory.Resolve(Env(("LOCALAPPDATA", LocalAppData)));

        Assert.Equal(Path.Combine(LocalAppData, "GlyphLens", "runtime"), location.Root);
        Assert.Null(location.Problem);
    }

    [Fact]
    public void AnAbsoluteLocalOverrideWins()
    {
        var location = RuntimeDirectory.Resolve(Env((RuntimeDirectory.EnvironmentVariable, @"D:\gl\runtime"), ("LOCALAPPDATA", LocalAppData)));

        Assert.Equal(@"D:\gl\runtime", location.Root);
        Assert.Null(location.Problem);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData(@"..\runtime")]
    [InlineData(@"\\server\share\runtime")]
    [InlineData("//server/share/runtime")]
    [InlineData(@"\\?\C:\runtime")]
    [InlineData(@"\runtime")]
    [InlineData(@"C:runtime")]
    public void AnOverrideThatIsNotAnAbsoluteLocalPath_IsRefusedAndReported_NotReplacedByTheDefault(string value)
    {
        var location = RuntimeDirectory.Resolve(Env((RuntimeDirectory.EnvironmentVariable, value), ("LOCALAPPDATA", LocalAppData)));

        Assert.Null(location.Root);
        Assert.Contains(RuntimeDirectory.EnvironmentVariable, location.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyLocation_ThereIsNoRuntime_AndTheProblemIsNamed()
    {
        var location = RuntimeDirectory.Resolve(Env());

        Assert.Null(location.Root);
        Assert.Contains("LOCALAPPDATA", location.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeLocalAppData_IsNotUsed()
    {
        Assert.Null(RuntimeDirectory.Resolve(Env(("LOCALAPPDATA", "AppData"))).Root);
    }

    [Fact]
    public void EverythingTheSetupWrites_IsInsideTheRuntimeFolder()
    {
        const string root = @"D:\rt";
        string[] paths =
        [
            RuntimeDirectory.ToolsFolder(root), RuntimeDirectory.UvExecutable(root), RuntimeDirectory.PythonInstallFolder(root),
            RuntimeDirectory.CacheFolder(root), RuntimeDirectory.VirtualEnvironmentFolder(root), RuntimeDirectory.VirtualEnvironmentPython(root),
            RuntimeDirectory.DownloadsFolder(root), RuntimeDirectory.LogFile(root), RuntimeDirectory.LockFile(root), RuntimeDirectory.LockMarker(root),
        ];

        Assert.All(paths, p => Assert.StartsWith(root + Path.DirectorySeparatorChar, p, StringComparison.Ordinal));
        Assert.Equal(@"D:\rt\venv\Scripts\python.exe", RuntimeDirectory.VirtualEnvironmentPython(root));
        Assert.Equal(@"D:\rt\tools\uv.exe", RuntimeDirectory.UvExecutable(root));
        Assert.Equal(@"D:\rt\setup.log", RuntimeDirectory.LogFile(root));
        Assert.Throws<ArgumentException>(() => RuntimeDirectory.LogFile(" "));
    }

    [Theory]
    [InlineData(@"C:\a", true)]
    [InlineData(@"D:\", true)]
    [InlineData("a", false)]
    [InlineData(@"C:a", false)]
    [InlineData(@"\\server\x", false)]
    [InlineData("//server/x", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    [InlineData("C:\\a\0b", false)]
    public void LocalPaths_AcceptsOnlyFullyQualifiedLocalPaths(string? value, bool expected) =>
        Assert.Equal(expected, LocalPaths.IsLocalAbsolute(value));
}
