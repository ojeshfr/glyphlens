namespace GlyphLens.Infrastructure.IPC;

/// <summary>
/// Thrown when a message does not conform to the GlyphLens worker protocol.
/// </summary>
public class WorkerProtocolException : Exception
{
    public WorkerProtocolException()
    {
    }

    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
