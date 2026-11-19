using System.Reflection;
using System.Runtime.Versioning;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules in docs/ARCHITECTURE.md for GlyphLens.Infrastructure.
/// </summary>
public class InfrastructureDependencyTests
{
    private static readonly Assembly InfrastructureAssembly = typeof(WorkerMessageSerializer).Assembly;

    [Fact]
    public void Infrastructure_ReferencesCore()
    {
        Assert.Contains(InfrastructureAssembly.GetReferencedAssemblies(), a => a.Name == "GlyphLens.Core");
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceUiOrWindowsAdapterAssemblies()
    {
        string[] forbidden =
        [
            "GlyphLens.App", "GlyphLens", "GlyphLens.Windows",
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms",
        ];

        var references = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.Empty(references.Intersect(forbidden, StringComparer.Ordinal));
    }

    [Fact]
    public void Infrastructure_IsPlatformIndependentForNow()
    {
        Assert.Null(InfrastructureAssembly.GetCustomAttribute<TargetPlatformAttribute>());
    }
}
