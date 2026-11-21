namespace GlyphLens.Core.Setup;

/// <summary>Path rules shared by the folder overrides (<c>GLYPHLENS_MODELS_DIR</c>, <c>GLYPHLENS_RUNTIME_DIR</c>).</summary>
public static class LocalPaths
{
    /// <summary>
    /// Whether <paramref name="value"/> is a fully qualified local path: not relative, not a UNC or device path (<c>\\server</c>,
    /// <c>\\?\</c>, <c>//</c>) and without NUL characters.
    /// </summary>
    public static bool IsLocalAbsolute(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('\0', StringComparison.Ordinal)
        && !value.StartsWith(@"\\", StringComparison.Ordinal)
        && !value.StartsWith("//", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(value);
}
