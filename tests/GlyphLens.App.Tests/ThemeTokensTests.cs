using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GlyphLens.App.Tests;

/// <summary>
/// Keeps the visual design honest: the light and dark palettes define the same tokens (the light values are DESIGN.md's), no other XAML or
/// code file hard-codes a colour, every view refers to brushes dynamically (so the appearance can switch while the app runs), and the
/// text/background pairs the UI relies on are readable (WCAG contrast) in both appearances.
/// </summary>
public sealed partial class ThemeTokensTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string> Palettes => new() { "Light", "Dark" };

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "GlyphLens.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root (GlyphLens.sln) was not found above the test binaries.");
    }

    private static string AppDirectory() => Path.Combine(RepositoryRoot(), "src", "GlyphLens.App");

    private static string PalettePath(string palette) => Path.Combine(AppDirectory(), "Themes", $"Palette.{palette}.xaml");

    private static bool IsPalette(string file) => Path.GetFileName(file).StartsWith("Palette.", StringComparison.Ordinal);

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static Dictionary<string, string> Colours(string palette)
    {
        var document = XDocument.Load(PalettePath(palette));
        return document.Root!.Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);
    }

    private static HashSet<string> Brushes(string palette) =>
        XDocument.Load(PalettePath(palette)).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => (string)e.Attribute(Xaml + "Key")!)
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("WindowBackgroundColor", "#F5F5F7")]
    [InlineData("SurfaceColor", "#FFFFFF")]
    [InlineData("SurfaceSecondaryColor", "#F9F9FB")]
    [InlineData("TextPrimaryColor", "#1D1D1F")]
    [InlineData("TextSecondaryColor", "#636366")]
    [InlineData("TextMutedColor", "#86868B")]
    [InlineData("BorderSubtleColor", "#E5E5EA")]
    [InlineData("AccentColor", "#0066CC")]
    [InlineData("AccentHoverColor", "#0055B3")]
    [InlineData("SuccessColor", "#248A3D")]
    [InlineData("WarningColor", "#C93400")]
    [InlineData("ErrorColor", "#D70015")]
    public void TheLightTokens_HaveTheValuesOfTheDesign(string key, string expected) =>
        Assert.Equal(expected, Colours("Light")[key]);

    [Theory]
    [InlineData("WindowBackgroundColor", "#1C1C1E")]
    [InlineData("SurfaceColor", "#2C2C2E")]
    [InlineData("TextPrimaryColor", "#F5F5F7")]
    [InlineData("TextSecondaryColor", "#AEAEB2")]
    [InlineData("AccentTextColor", "#64B5FF")]
    public void TheDarkTokens_HaveTheValuesOfTheDesign(string key, string expected) =>
        Assert.Equal(expected, Colours("Dark")[key]);

    [Fact]
    public void BothPalettes_DefineExactlyTheSameKeys()
    {
        Assert.Equal(Colours("Light").Keys.Order(), Colours("Dark").Keys.Order());
        Assert.Equal(Brushes("Light").Order(), Brushes("Dark").Order());
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryColourToken_HasABrush(string palette)
    {
        var brushes = Brushes(palette);
        foreach (string colour in Colours(palette).Keys.Where(k => k is not "ShadowColor"))
        {
            Assert.Contains(colour[..^"Color".Length] + "Brush", brushes);
        }
    }

    [Fact]
    public void NoOtherXamlFile_HardCodesAColour()
    {
        var offenders = XamlFiles()
            .Where(f => !IsPalette(f))
            .Where(f => HexColour().IsMatch(File.ReadAllText(f)) || NamedBrush().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(AppDirectory(), f))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheViews_ReferToBrushesDynamically_SoTheAppearanceCanSwitchWhileRunning()
    {
        var offenders = XamlFiles()
            .Where(f => !IsPalette(f))
            .SelectMany(f => StaticBrush().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoCodeFile_HardCodesAColour()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(AppDirectory(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"Color\.From(Rgb|Argb)\(\s*(\d+\s*,\s*)?0x", RegexOptions.CultureInvariant) || Regex.IsMatch(text, "\"#[0-9A-Fa-f]{6,8}\"", RegexOptions.CultureInvariant))
            {
                offenders.Add(Path.GetRelativePath(AppDirectory(), file));
            }
        }

        Assert.Empty(offenders);
    }

    public static TheoryData<string, string, string, double> ContrastPairs()
    {
        (string Foreground, string Background, double Minimum)[] pairs =
        [
            ("TextPrimaryColor", "SurfaceColor", 7.0),
            ("TextPrimaryColor", "WindowBackgroundColor", 7.0),
            ("TextSecondaryColor", "SurfaceColor", 4.5),
            ("TextSecondaryColor", "WindowBackgroundColor", 4.5),
            ("TextSecondaryColor", "SurfaceSecondaryColor", 4.5),
            ("TextPrimaryColor", "SidebarBackgroundColor", 7.0),
            ("TextSecondaryColor", "SidebarBackgroundColor", 4.5),
            ("TextPrimaryColor", "ControlFillColor", 7.0),
            ("TextPrimaryColor", "ControlFillPressedColor", 4.5),
            ("TextPrimaryColor", "OverlayBubbleColor", 7.0),
            ("TextSecondaryColor", "OverlayBubbleColor", 4.5),
            ("AccentTextColor", "SurfaceColor", 4.5),
            ("AccentTextColor", "WindowBackgroundColor", 4.5),
            ("AccentTextColor", "OverlayBubbleColor", 4.5),
            ("AccentTextColor", "AccentSubtleColor", 4.5),
            ("OnAccentColor", "AccentColor", 4.5),
            ("OnAccentColor", "AccentHoverColor", 4.5),
            ("WarningTextColor", "WarningSubtleColor", 4.5),
            ("WarningTextColor", "SurfaceColor", 4.5),
            ("ErrorTextColor", "ErrorSubtleColor", 4.5),
            ("ErrorColor", "SurfaceColor", 4.5),
            ("SuccessColor", "SurfaceColor", 3.0),
            ("WarningColor", "SurfaceColor", 3.0),
            ("OnStatusColor", "SuccessColor", 3.0),
            ("OnStatusColor", "WarningColor", 3.0),
            ("OnStatusColor", "ErrorColor", 3.0),
            ("AccentTextColor", "SidebarBackgroundColor", 3.0),
            ("TextPrimaryColor", "SegmentSelectedColor", 4.5),
        ];
        var data = new TheoryData<string, string, string, double>();
        foreach (string palette in new[] { "Light", "Dark" })
        {
            foreach (var (foreground, background, minimum) in pairs)
            {
                data.Add(palette, foreground, background, minimum);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ContrastPairs))]
    public void TextColoursUsedOnTheirBackgrounds_AreReadable(string palette, string foreground, string background, double minimum)
    {
        var colours = Colours(palette);

        double ratio = Contrast(colours[foreground], colours[background]);

        Assert.True(ratio >= minimum, $"{palette}: {foreground} on {background} is {ratio:0.00}:1, below {minimum}:1");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void TheMutedTextColour_IsOnlyForNonEssentialInformation_AndIsBelowAaOnTheSurface(string palette)
    {
        // TextMuted is for tertiary information. It does not reach 4.5:1 on the surface, so no essential text may use it: the check below
        // fails if a view ever sets a Foreground to it (use TextSecondary instead).
        Assert.True(Contrast(Colours(palette)["TextMutedColor"], Colours(palette)["SurfaceColor"]) < 4.5);

        foreach (string file in XamlFiles().Where(f => !IsPalette(f)))
        {
            Assert.DoesNotMatch("Foreground=\"\\{(Static|Dynamic)Resource TextMutedBrush\\}\"", File.ReadAllText(file));
        }
    }

    [GeneratedRegex("(?<![A-Za-z0-9&])#[0-9A-Fa-f]{6,8}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexColour();

    [GeneratedRegex("(Foreground|Background|BorderBrush|Fill|Stroke)=\"(White|Black|Red|Green|Blue|Gray|Grey|Orange|Yellow)\"", RegexOptions.CultureInvariant)]
    private static partial Regex NamedBrush();

    [GeneratedRegex("\\{StaticResource \\w+Brush\\}", RegexOptions.CultureInvariant)]
    private static partial Regex StaticBrush();

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..]; // #AARRGGBB: the colour itself (the tests pair only opaque colours)
        }

        double Channel(int offset)
        {
            double c = int.Parse(value.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }
}
