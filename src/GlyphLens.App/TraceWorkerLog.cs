using System.Diagnostics;
using System.Globalization;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.App;

/// <summary>
/// Writes worker diagnostics to the .NET trace listeners (the debugger output window by default).
/// </summary>
/// <remarks>Entries carry request ids, message types, outcomes and durations only: never message payloads or screen text.</remarks>
internal sealed class TraceWorkerLog : IWorkerLog
{
    public void Request(WorkerRequestLogEntry entry)
    {
        Trace.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"worker req={entry.RequestId} type={entry.MessageType} outcome={entry.Outcome}{(entry.ErrorCode is null ? string.Empty : " code=" + entry.ErrorCode)} ms={entry.Duration.TotalMilliseconds:0.0}"));
    }

    public void Info(string message) => Trace.WriteLine("worker " + message);
}
