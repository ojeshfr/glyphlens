namespace GlyphLens.Core.Setup;

/// <summary>
/// One file the setup downloads, pinned by its exact size and SHA-256. The pinned hash is the trust anchor: a file whose bytes do not
/// match is never kept.
/// </summary>
/// <param name="Id">A stable identifier (used in logs and tests).</param>
/// <param name="DisplayName">What the file is, in plain words.</param>
/// <param name="Url">The HTTPS address it is fetched from.</param>
/// <param name="Size">The exact size in bytes.</param>
/// <param name="Sha256">The SHA-256 of the file, 64 lowercase hex characters.</param>
/// <param name="Licence">The licence the publisher states for it.</param>
public sealed record PinnedFile(string Id, string DisplayName, Uri Url, long Size, string Sha256, string Licence)
{
    /// <summary>The host the file comes from, for the consent text.</summary>
    public string Host => Url.Host;
}

/// <summary>A pinned file and where (relative to the models directory) it is installed.</summary>
/// <param name="File">The pinned download.</param>
/// <param name="RelativePath">The destination path relative to the models directory.</param>
public sealed record PinnedModelFile(PinnedFile File, string RelativePath);

/// <summary>
/// Everything the one-click setup ("Install everything") downloads, in one place. Every URL, size and hash here was checked by
/// downloading the file; nothing else is ever downloaded by GlyphLens' own code (uv then downloads the Python build it pins itself and
/// the packages listed with their hashes in the engine lock file, see <c>worker/requirements/engines.lock.txt</c>).
/// </summary>
/// <remarks>
/// Change this file only together with a real download of the new files (see docs/DEVELOPMENT.md, "One-click setup").
/// </remarks>
public static class SetupManifest
{
    /// <summary>The uv release used to create the Python environment (published 2026-11-03; at least 14 days old when pinned).</summary>
    public const string UvVersion = "0.13.7";

    /// <summary>uv's own executable inside <see cref="Uv"/>: the only entry that is extracted.</summary>
    public const string UvExecutableName = "uv.exe";

    /// <summary>The size of <see cref="UvExecutableName"/> inside the pinned archive.</summary>
    public const long UvExecutableSize = 31_803_184;

    /// <summary>The SHA-256 of <see cref="UvExecutableName"/> inside the pinned archive (computed from the verified archive).</summary>
    public const string UvExecutableSha256 = "1791d7a9ed5b6df13ccf80f102e025c333010d96326b4ff0262aa3b578835c5a";

    /// <summary>
    /// The Python that uv installs (a CPython build from python-build-standalone, chosen and hash-checked by the pinned uv). The worker's
    /// engines were tested on Python 3.12; this is the newest 3.12 that uv <see cref="UvVersion"/> knows.
    /// </summary>
    public const string PythonRequest = "cpython-3.12.16-windows-x86_64-none";

    /// <summary>The Python minor version the engine lock file is made for.</summary>
    public static Version PythonVersion { get; } = new(3, 12);

    /// <summary>The package index the locked packages come from.</summary>
    public static Uri PackageIndex { get; } = new("https://pypi.org/simple");

    /// <summary>Free space needed on the drive of the runtime folder before the setup starts (about 1.5 GB).</summary>
    public const long RequiredFreeBytes = 1_536L * 1024 * 1024;

    /// <summary>Free space needed on the drive of the models folder when it is not the runtime's drive.</summary>
    public const long RequiredFreeBytesForModels = 384L * 1024 * 1024;

    /// <summary>Size of the Python packages uv downloads (the 70 locked Windows x64 wheels: 151,717,276 bytes), for the consent text.</summary>
    public const long ApproximatePackageDownloadBytes = 151_717_276;

    /// <summary>Size of the Python build uv downloads (22,014,416 bytes for the pinned build), for the consent text.</summary>
    public const long ApproximatePythonDownloadBytes = 22_014_416;

    /// <summary>The servers the setup contacts, and for what (for the consent text).</summary>
    public static IReadOnlyList<(string Host, string Purpose)> Hosts { get; } =
    [
        ("github.com", "uv (its release files are served from GitHub's download servers)"),
        ("releases.astral.sh", "the Python 3.12 build, fetched by uv"),
        ("pypi.org, files.pythonhosted.org", "the OCR and translation packages, fetched by uv"),
        ("huggingface.co", "the PP-OCRv5 model files"),
        ("argos-net.com", "the translation models"),
    ];

    /// <summary>The uv tool (Astral), official GitHub release.</summary>
    public static PinnedFile Uv { get; } = new(
        "uv",
        $"uv {UvVersion} (Python installer by Astral)",
        new Uri($"https://github.com/astral-sh/uv/releases/download/{UvVersion}/uv-x86_64-pc-windows-msvc.zip"),
        15_236_317,
        "0cdc5b38fe5a8168ed6a25814f5ba43c21d313dd3187314fccb37b7cec46e021",
        "MIT or Apache-2.0");

    /// <summary>PP-OCRv5 mobile detection and recognition models (official ONNX exports, pinned to a Hugging Face commit).</summary>
    public static IReadOnlyList<PinnedModelFile> OcrFiles { get; } =
    [
        Ocr("ocr-det-onnx", "PP-OCRv5_mobile_det_onnx", "e6f4fa85f00e168c862bc462aebca69eef9b3d3d", "inference.onnx", 4_826_518, "a431985659dc921974177a95adcfbb90fd9e51989a5e04d70d0b75f597b6e61d"),
        Ocr("ocr-det-yml", "PP-OCRv5_mobile_det_onnx", "e6f4fa85f00e168c862bc462aebca69eef9b3d3d", "inference.yml", 903, "98069072e1b6b37d727fd9d9f11725faa46d6ea0de012f2ed26caea011c37699"),
        Ocr("ocr-rec-onnx", "PP-OCRv5_mobile_rec_onnx", "ed152b8b495f84de93cda5709d768548a9127622", "inference.onnx", 16_534_782, "da72dc72ca4dc220df0dfde68c1dedc31c58d3e76a25871122e5056227d50092"),
        Ocr("ocr-rec-yml", "PP-OCRv5_mobile_rec_onnx", "ed152b8b495f84de93cda5709d768548a9127622", "inference.yml", 148_345, "5dfeb2777f6d0db8177d8128a8acfcf6e6276dc4ac73ea3bf0dc06d6a5e85d8e"),
    ];

    /// <summary>The Argos Translate Simplified Chinese to English package (version 1.9).</summary>
    public static PinnedFile SimplifiedChineseModel { get; } = new(
        "model-zh",
        "Simplified Chinese → English model (Argos Translate 1.9)",
        new Uri("https://argos-net.com/v1/translate-zh_en-1_9.argosmodel"),
        74_481_402,
        "62e7af5a3a48b530e47b7b3e5c78c2de79073ecd815750d2bf3ab35b4a67da2d",
        "CC-BY 4.0");

    /// <summary>The Argos Translate Traditional Chinese to English package (version 1.9), optional.</summary>
    public static PinnedFile TraditionalChineseModel { get; } = new(
        "model-zt",
        "Traditional Chinese → English model (Argos Translate 1.9)",
        new Uri("https://argos-net.com/v1/translate-zt_en-1_9.argosmodel"),
        74_498_034,
        "a9ea826d801f059dd47f1f47b6c1dc9d762519a80ea3afd2a982130a42a25486",
        "CC-BY 4.0");

    /// <summary>Every file GlyphLens' own code downloads, for checks and tests.</summary>
    public static IReadOnlyList<PinnedFile> AllFiles { get; } =
        [Uv, .. OcrFiles.Select(f => f.File), SimplifiedChineseModel, TraditionalChineseModel];

    /// <summary>The total bytes of the OCR model files.</summary>
    public static long OcrBytes => OcrFiles.Sum(f => f.File.Size);

    private static PinnedModelFile Ocr(string id, string repository, string commit, string fileName, long size, string sha256) => new(
        new PinnedFile(
            id,
            $"{repository}/{fileName} (PaddleOCR PP-OCRv5)",
            new Uri($"https://huggingface.co/PaddlePaddle/{repository}/resolve/{commit}/{fileName}"),
            size,
            sha256,
            "Apache-2.0"),
        Path.Combine("ocr", repository, fileName));
}
