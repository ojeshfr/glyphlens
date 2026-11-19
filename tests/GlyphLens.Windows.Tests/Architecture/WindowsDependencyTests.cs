using System.Reflection;
using System.Runtime.Versioning;

namespace GlyphLens.Windows.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules in docs/ARCHITECTURE.md for GlyphLens.Windows.
/// </summary>
/// <remarks>
/// GlyphLens.Windows has no adapters yet (they arrive in Phase 1 and 2), so these tests check
/// the assembly's platform targeting and that it stays free of UI and upper-layer dependencies.
/// </remarks>
public class WindowsDependencyTests
{
    private static readonly Assembly WindowsAssembly = Assembly.Load("GlyphLens.Windows");

    [Fact]
    public void Windows_TargetsTheWindowsPlatform()
    {
        var target = WindowsAssembly.GetCustomAttribute<TargetPlatformAttribute>();

        Assert.NotNull(target);
        Assert.StartsWith("Windows", target.PlatformName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_DoesNotReferenceWpfOrUpperLayers()
    {
        string[] forbidden =
        [
            "GlyphLens.App", "GlyphLens", "GlyphLens.Infrastructure",
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "System.Windows.Forms",
        ];

        var references = WindowsAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.Empty(references.Intersect(forbidden, StringComparer.Ordinal));
    }
}
