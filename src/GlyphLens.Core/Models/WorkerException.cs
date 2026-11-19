namespace GlyphLens.Core.Models;

/// <summary>Category of a worker failure.</summary>
public enum WorkerErrorKind
{
    /// <summary>No suitable Python interpreter (3.11 or newer) was found.</summary>
    PythonNotFound = 1,

    /// <summary>The worker package could not be found.</summary>
    WorkerNotFound,

    /// <summary>The worker process could not be started.</summary>
    LaunchFailed,

    /// <summary>The worker did not connect to the pipe in time.</summary>
    ConnectTimeout,

    /// <summary>The worker process exited before or during the operation.</summary>
    ProcessExited,

    /// <summary>The initialization handshake failed or the client could not be authenticated.</summary>
    HandshakeFailed,

    /// <summary>The peer sent data that violates the protocol.</summary>
    ProtocolViolation,

    /// <summary>The worker did not answer a request in time.</summary>
    RequestTimeout,

    /// <summary>The pipe closed or broke.</summary>
    ConnectionLost,

    /// <summary>The worker answered with an error response.</summary>
    Rejected,

    /// <summary>The worker is not running or not connected.</summary>
    NotConnected,

    /// <summary>Too many requests are already waiting for an answer.</summary>
    TooManyRequests,
}

/// <summary>
/// A failure talking to, starting or stopping the worker. Messages never contain message payloads.
/// </summary>
public sealed class WorkerException : Exception
{
    public WorkerException()
        : this(WorkerErrorKind.ConnectionLost, "Worker error.")
    {
    }

    public WorkerException(string message)
        : this(WorkerErrorKind.ConnectionLost, message)
    {
    }

    public WorkerException(string message, Exception innerException)
        : this(WorkerErrorKind.ConnectionLost, message, null, innerException)
    {
    }

    public WorkerException(WorkerErrorKind kind, string message, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ErrorCode = errorCode;
    }

    public WorkerErrorKind Kind { get; }

    /// <summary>For <see cref="WorkerErrorKind.Rejected"/>: the protocol error code (for example <c>not_initialized</c>).</summary>
    public string? ErrorCode { get; }
}
