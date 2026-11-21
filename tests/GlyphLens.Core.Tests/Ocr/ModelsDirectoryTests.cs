using GlyphLens.Core.Ocr;

namespace GlyphLens.Core.Tests.Ocr;

public sealed class ModelsDirectoryTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void AnAbsoluteOverrideWins() =>
        Assert.Equal(@"D:\models", ModelsDirectory.Resolve(Env((ModelsDirectory.EnvironmentVariable, @"D:\models"), ("LOCALAPPDATA", @"C:\Users\a\AppData\Local"))));

    [Theory]
    [InlineData(@"models")]
    [InlineData(@"\\server\share\models")]
    [InlineData("//server/share/models")]
    [InlineData(@"\models")]
    public void AnOverrideThatIsNotAnAbsoluteLocalPath_IsIgnored(string value) =>
        Assert.Equal(
            Path.Combine(@"C:\Users\a\AppData\Local", "GlyphLens", "models"),
            ModelsDirectory.Resolve(Env((ModelsDirectory.EnvironmentVariable, value), ("LOCALAPPDATA", @"C:\Users\a\AppData\Local"))));

    [Fact]
    public void WithoutAnOverride_TheDefaultIsUnderLocalAppData() =>
        Assert.Equal(
            Path.Combine(@"C:\Users\a\AppData\Local", "GlyphLens", "models"),
            ModelsDirectory.Resolve(Env(("LOCALAPPDATA", @"C:\Users\a\AppData\Local"))));

    [Fact]
    public void WithoutAnyLocation_ThereIsNoDirectory() =>
        Assert.Null(ModelsDirectory.Resolve(Env()));

    [Fact]
    public void TheOcrFolderIsAChildOfTheModelsDirectory() =>
        Assert.Equal(Path.Combine(@"D:\models", "ocr"), ModelsDirectory.OcrFolder(@"D:\models"));

    [Fact]
    public void TheExpectedModelFoldersAreTheOfficialOnnxExports() =>
        Assert.Equal(["PP-OCRv5_mobile_det_onnx", "PP-OCRv5_mobile_rec_onnx"], ModelsDirectory.OcrModelFolders);
}
