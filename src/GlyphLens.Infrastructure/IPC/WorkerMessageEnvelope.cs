using System.Text.Json;

namespace GlyphLens.Infrastructure.IPC;

/// <summary>
/// The versioned envelope that wraps every message exchanged with the local worker.
/// </summary>
/// <remarks>
/// Wire format: one UTF-8 JSON object per message, camelCase field names. See docs/IPC-PROTOCOL.md.
/// This type defines the message shape only; the Named Pipes transport is not implemented yet.
/// </remarks>
public sealed record WorkerMessageEnvelope(
    string ProtocolVersion,
    string RequestId,
    string MessageType,
    JsonElement? Payload,
    DateTimeOffset Timestamp);
