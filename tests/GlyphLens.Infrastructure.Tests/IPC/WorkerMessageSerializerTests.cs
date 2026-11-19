using System.Text;
using System.Text.Json;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

public class WorkerMessageSerializerTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static WorkerMessageEnvelope Envelope(
        string version = "1.0",
        string requestId = "req-1",
        string messageType = WorkerMessageTypes.WorkerHealth,
        JsonElement? payload = null,
        DateTimeOffset? timestamp = null) =>
        new(version, requestId, messageType, payload, timestamp ?? SampleTime);

    private static WorkerMessageEnvelope Decode(string json) =>
        WorkerMessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Serialize_UsesDocumentedCamelCaseFieldNames()
    {
        var bytes = WorkerMessageSerializer.Serialize(Envelope());

        using var document = JsonDocument.Parse(bytes);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["protocolVersion", "requestId", "messageType", "payload", "timestamp"], names);
        Assert.Equal("2026-01-15T10:30:00+00:00", document.RootElement.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void RoundTrip_PreservesEnvelopeAndPayload()
    {
        using var payload = JsonDocument.Parse("""{"text":"Hola","targetLanguage":"en"}""");
        var original = Envelope(messageType: WorkerMessageTypes.TranslationTranslate, payload: payload.RootElement);

        var decoded = WorkerMessageSerializer.Deserialize(WorkerMessageSerializer.Serialize(original));

        Assert.Equal(original.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.MessageType, decoded.MessageType);
        Assert.Equal(original.Timestamp, decoded.Timestamp);
        Assert.Equal("Hola", decoded.Payload!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public void Deserialize_AcceptsMessageFromWorker()
    {
        var envelope = Decode("""
            {"protocolVersion":"1.0","requestId":"abc","messageType":"worker.health.result",
             "payload":{"status":"unavailable"},"timestamp":"2026-01-15T10:30:00Z"}
            """);

        Assert.Equal("worker.health.result", envelope.MessageType);
        Assert.Equal(TimeSpan.Zero, envelope.Timestamp.Offset);
    }

    [Fact]
    public void Deserialize_AcceptsNullPayloadAndNewerMinorVersion()
    {
        var envelope = Decode("""
            {"protocolVersion":"1.4","requestId":"abc","messageType":"worker.shutdown",
             "payload":null,"timestamp":"2026-01-15T10:30:00Z"}
            """);

        Assert.Null(envelope.Payload);
        Assert.Equal("1.4", envelope.ProtocolVersion);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownFieldsForForwardCompatibility()
    {
        var envelope = Decode("""
            {"protocolVersion":"1.1","requestId":"abc","messageType":"worker.health",
             "payload":{},"timestamp":"2026-01-15T10:30:00Z","futureField":true}
            """);

        Assert.Equal("abc", envelope.RequestId);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("requestId")]
    [InlineData("messageType")]
    [InlineData("payload")]
    [InlineData("timestamp")]
    public void Deserialize_RejectsMissingRequiredField(string field)
    {
        var fields = new Dictionary<string, string>
        {
            ["protocolVersion"] = "\"1.0\"",
            ["requestId"] = "\"abc\"",
            ["messageType"] = "\"worker.health\"",
            ["payload"] = "{}",
            ["timestamp"] = "\"2026-01-15T10:30:00Z\"",
        };
        fields.Remove(field);
        var json = "{" + string.Join(",", fields.Select(f => $"\"{f.Key}\":{f.Value}")) + "}";

        Assert.Throws<WorkerProtocolException>(() => Decode(json));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("1")]
    [InlineData("latest")]
    public void Deserialize_RejectsUnsupportedOrMalformedVersion(string version)
    {
        var json = $$"""
            {"protocolVersion":"{{version}}","requestId":"abc","messageType":"worker.health",
             "payload":{},"timestamp":"2026-01-15T10:30:00Z"}
            """;

        Assert.Throws<WorkerProtocolException>(() => Decode(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void Deserialize_RejectsNonEnvelopeInput(string json)
    {
        Assert.Throws<WorkerProtocolException>(() => Decode(json));
    }

    [Fact]
    public void Deserialize_RejectsOversizedMessage()
    {
        var oversized = new byte[WorkerMessageSerializer.MaxMessageBytes + 1];

        Assert.Throws<WorkerProtocolException>(() => WorkerMessageSerializer.Deserialize(oversized));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankRequestId(string requestId)
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerMessageSerializer.Validate(Envelope(requestId: requestId)));
    }

    [Fact]
    public void Validate_RejectsOverlongRequestId()
    {
        var requestId = new string('x', WorkerMessageSerializer.MaxRequestIdLength + 1);

        Assert.Throws<WorkerProtocolException>(() => WorkerMessageSerializer.Validate(Envelope(requestId: requestId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Worker.Health")]
    [InlineData("worker..health")]
    [InlineData("worker health")]
    [InlineData(".worker")]
    public void Validate_RejectsMalformedMessageType(string messageType)
    {
        Assert.Throws<WorkerProtocolException>(
            () => WorkerMessageSerializer.Validate(Envelope(messageType: messageType)));
    }

    [Fact]
    public void Validate_RejectsMissingTimestamp()
    {
        Assert.Throws<WorkerProtocolException>(
            () => WorkerMessageSerializer.Validate(Envelope(timestamp: default(DateTimeOffset))));
    }

    [Fact]
    public void Serialize_RefusesInvalidEnvelope()
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerMessageSerializer.Serialize(Envelope(version: "2.0")));
    }

    [Fact]
    public void Requests_ContainsEveryDocumentedRequestType()
    {
        string[] documented =
        [
            "worker.initialize", "worker.health", "worker.shutdown", "ocr.detect",
            "translation.translate", "translation.cancel", "models.list", "models.status",
        ];

        Assert.Equal(documented.Order(StringComparer.Ordinal), WorkerMessageTypes.Requests.Order(StringComparer.Ordinal));
    }
}
