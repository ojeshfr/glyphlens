namespace GlyphLens.Windows.Tests.Integration;

/// <summary>
/// Marks a test that needs a real, interactive Windows desktop (GPU, displays, windows).
/// </summary>
/// <remarks>
/// These tests are skipped unless the environment variable <c>GLYPHLENS_RUN_CAPTURE_TESTS</c> is set to <c>1</c>,
/// so ordinary CI runs (which have no interactive desktop) never execute or silently "pass" them. To run:
/// <c>$env:GLYPHLENS_RUN_CAPTURE_TESTS = "1"; dotnet test tests/GlyphLens.Windows.Tests</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "GLYPHLENS_RUN_CAPTURE_TESTS";

    public HardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Requires an interactive Windows desktop. Set {EnvironmentVariable}=1 to run.";
        }
    }
}
