using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using GlyphLens.Core.Contracts;

namespace GlyphLens.Infrastructure.IPC;

/// <summary>
/// Encodes and decodes <see cref="WorkerMessageEnvelope"/> messages and validates them
/// against the protocol rules in docs/IPC-PROTOCOL.md.
/// </summary>
public static partial class WorkerMessageSerializer
{
    /// <summary>Largest accepted encoded message, in bytes. Protects against unbounded reads.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Longest accepted request id.</summary>
    public const int MaxRequestIdLength = 128;

    /// <summary>JSON options shared by every GlyphLens IPC payload: camelCase names, string enums.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize(WorkerMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(envelope);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (bytes.Length > MaxMessageBytes)
        {
            throw new WorkerProtocolException($"Encoded message exceeds the {MaxMessageBytes}-byte limit.");
        }

        return bytes;
    }

    public static WorkerMessageEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxMessageBytes)
        {
            throw new WorkerProtocolException($"Message exceeds the {MaxMessageBytes}-byte limit.");
        }

        WorkerMessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WorkerMessageEnvelope>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw new WorkerProtocolException("Message is not a valid protocol envelope.", ex);
        }

        if (envelope is null)
        {
            throw new WorkerProtocolException("Message is empty.");
        }

        Validate(envelope);
        return envelope;
    }

    /// <summary>Checks envelope fields and protocol-version compatibility with <see cref="ProtocolVersion.Current"/>.</summary>
    public static void Validate(WorkerMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ProtocolVersion.TryParse(envelope.ProtocolVersion, out var version))
        {
            throw new WorkerProtocolException($"Invalid protocolVersion '{envelope.ProtocolVersion}'.");
        }

        if (!version.IsCompatibleWith(ProtocolVersion.Current))
        {
            throw new WorkerProtocolException(
                $"Unsupported protocolVersion '{version}'; this build speaks {ProtocolVersion.Current}.");
        }

        if (string.IsNullOrWhiteSpace(envelope.RequestId) || envelope.RequestId.Length > MaxRequestIdLength)
        {
            throw new WorkerProtocolException($"requestId must be 1-{MaxRequestIdLength} non-blank characters.");
        }

        if (envelope.MessageType is null || !MessageTypePattern().IsMatch(envelope.MessageType))
        {
            throw new WorkerProtocolException($"Invalid messageType '{envelope.MessageType}'.");
        }

        if (envelope.Timestamp == default)
        {
            throw new WorkerProtocolException("timestamp is required.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    // Dotted lowercase segments, e.g. "worker.health", "translation.translate.result", "error".
    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageTypePattern();
}
