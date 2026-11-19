using System.Reflection;
using System.Runtime.Versioning;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules in docs/ARCHITECTURE.md: GlyphLens.Core is platform-independent.
/// </summary>
public class CoreDependencyTests
{
    private static readonly Assembly CoreAssembly = typeof(ScreenTextRegion).Assembly;

    [Fact]
    public void Core_DoesNotTargetASpecificOperatingSystem()
    {
        Assert.Null(CoreAssembly.GetCustomAttribute<TargetPlatformAttribute>());
        Assert.Null(CoreAssembly.GetCustomAttribute<SupportedOSPlatformAttribute>());
    }

    [Fact]
    public void Core_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name is "netstandard" or "mscorlib" || name.StartsWith("System", StringComparison.Ordinal),
            $"GlyphLens.Core must not reference '{name}'."));
    }

    [Fact]
    public void Core_DoesNotReferenceOtherGlyphLensProjects()
    {
        var glyphLensReferences = CoreAssembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("GlyphLens", StringComparison.Ordinal));

        Assert.Empty(glyphLensReferences);
    }
}
