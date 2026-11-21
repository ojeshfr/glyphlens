namespace GlyphLens.Core.Setup;

/// <summary>The steps of the one-click setup, in the order they run.</summary>
public enum SetupStepId
{
    /// <summary>Check the folders and the free disk space.</summary>
    Prepare,

    /// <summary>Download and verify uv, extract <c>uv.exe</c>.</summary>
    Uv,

    /// <summary>Install the Python build and create the virtual environment.</summary>
    Python,

    /// <summary>Install the hash-locked OCR and translation packages.</summary>
    Packages,

    /// <summary>Download the PP-OCRv5 model files.</summary>
    OcrModels,

    /// <summary>Restart the worker on the new environment.</summary>
    Worker,

    /// <summary>Download and import the Simplified Chinese model.</summary>
    SimplifiedModel,

    /// <summary>Download and import the Traditional Chinese model (optional).</summary>
    TraditionalModel,

    /// <summary>Ask the worker whether OCR and translation are now available.</summary>
    Check,
}

/// <summary>Where a step stands.</summary>
public enum SetupStepState
{
    Pending,

    Running,

    /// <summary>The step did its work and verified it.</summary>
    Done,

    /// <summary>Already in place and verified; nothing was downloaded or changed.</summary>
    Skipped,

    Failed,
}

/// <summary>A progress report: the step, its state, a short message, and the bytes of a running download.</summary>
public sealed record SetupProgress(SetupStepId Step, SetupStepState State, string Message, long? BytesDone = null, long? BytesTotal = null);

/// <summary>What the user chose in the consent panel.</summary>
/// <param name="IncludeTraditional">Also install the Traditional Chinese model.</param>
public sealed record SetupOptions(bool IncludeTraditional);

/// <summary>What a setup run will do, for the consent panel.</summary>
/// <param name="Steps">The steps, in order.</param>
/// <param name="Files">The files GlyphLens downloads itself (pinned by size and hash).</param>
/// <param name="ApproximateDownloadBytes">About how much will be downloaded in total, including what uv fetches.</param>
/// <param name="RuntimeFolder">Where the Python runtime goes.</param>
/// <param name="ModelsFolder">Where the model files go.</param>
/// <param name="LogFile">The technical log of the setup.</param>
/// <param name="PackageCount">How many hash-locked Python packages uv installs.</param>
public sealed record SetupPlan(
    IReadOnlyList<SetupStepId> Steps,
    IReadOnlyList<PinnedFile> Files,
    long ApproximateDownloadBytes,
    string RuntimeFolder,
    string ModelsFolder,
    string LogFile,
    int PackageCount);

/// <summary>The outcome of a setup run that did not fail.</summary>
/// <param name="Skipped">Steps that were already in place.</param>
/// <param name="Warnings">Things the user should know (plain words), for example a configured Python that is used instead.</param>
public sealed record SetupResult(IReadOnlyList<SetupStepId> Skipped, IReadOnlyList<string> Warnings);

/// <summary>A setup step failed. <see cref="Exception.Message"/> is plain language, safe to show, and never contains screen content.</summary>
public sealed class SetupException : Exception
{
    public SetupException()
        : this(SetupStepId.Prepare, "The setup failed.")
    {
    }

    public SetupException(string message)
        : this(SetupStepId.Prepare, message)
    {
    }

    public SetupException(string message, Exception innerException)
        : this(SetupStepId.Prepare, message, innerException)
    {
    }

    public SetupException(SetupStepId step, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Step = step;
    }

    /// <summary>The step that failed.</summary>
    public SetupStepId Step { get; }
}

/// <summary>Why a pinned download was refused.</summary>
public enum DownloadFailureKind
{
    /// <summary>The server or the network failed (connection, HTTP status, timeout).</summary>
    Network,

    /// <summary>Not HTTPS, or redirected to something that is not HTTPS.</summary>
    Insecure,

    /// <summary>The file is larger or smaller than pinned.</summary>
    SizeMismatch,

    /// <summary>The file's SHA-256 is not the pinned one.</summary>
    HashMismatch,

    /// <summary>The file could not be written.</summary>
    Storage,
}

/// <summary>A pinned download failed; nothing was kept. The message is plain language.</summary>
public sealed class DownloadException : Exception
{
    public DownloadException()
        : this(DownloadFailureKind.Network, "The download failed.")
    {
    }

    public DownloadException(string message)
        : this(DownloadFailureKind.Network, message)
    {
    }

    public DownloadException(string message, Exception innerException)
        : this(DownloadFailureKind.Network, message, innerException)
    {
    }

    public DownloadException(DownloadFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public DownloadFailureKind Kind { get; }
}
