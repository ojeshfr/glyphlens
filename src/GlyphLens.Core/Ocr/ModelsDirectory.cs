namespace GlyphLens.Core.Ocr;

/// <summary>
/// Where the worker keeps models: <c>GLYPHLENS_MODELS_DIR</c> when it is an absolute local path, otherwise
/// <c>%LOCALAPPDATA%\GlyphLens\models</c>. The application mirrors the worker's rule only to point the user at the folder
/// (never to read model files); the worker is the authority on what is installed.
/// </summary>
public static class ModelsDirectory
{
    /// <summary>The environment variable that overrides the models directory.</summary>
    public const string EnvironmentVariable = "GLYPHLENS_MODELS_DIR";

    /// <summary>The folder (inside the models directory) that holds the OCR model folders.</summary>
    public const string OcrSubdirectory = "ocr";

    /// <summary>The expected OCR model folder names, as the official ONNX exports are named.</summary>
    public static IReadOnlyList<string> OcrModelFolders { get; } = ["PP-OCRv5_mobile_det_onnx", "PP-OCRv5_mobile_rec_onnx"];

    /// <summary>Resolves the models directory, or <see langword="null"/> when no usable location exists.</summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable (injected for tests).</param>
    public static string? Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } overridePath && IsLocalAbsolute(overridePath))
        {
            return overridePath;
        }

        return getEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } localAppData && IsLocalAbsolute(localAppData)
            ? Path.Combine(localAppData, "GlyphLens", "models")
            : null;
    }

    /// <summary>The OCR models folder inside <paramref name="modelsDirectory"/>.</summary>
    public static string OcrFolder(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        return Path.Combine(modelsDirectory, OcrSubdirectory);
    }

    private static bool IsLocalAbsolute(string value) => Setup.LocalPaths.IsLocalAbsolute(value);
}
