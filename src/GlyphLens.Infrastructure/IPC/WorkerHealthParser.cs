using System.Text.Json;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Models;

namespace GlyphLens.Infrastructure.IPC;

/// <summary>
/// Reads the <c>worker.health.result</c> and <c>worker.initialize.result</c> payloads.
/// </summary>
/// <remarks>
/// Parsing is lenient about unknown fields (forward compatibility) but strict about types, and any state it does
/// not recognise becomes <see cref="WorkerComponentState.Unknown"/> rather than being assumed healthy.
/// </remarks>
public static class WorkerHealthParser
{
    public static WorkerHealth ParseHealth(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } root)
        {
            throw new WorkerException(WorkerErrorKind.ProtocolViolation, "The health response has no payload object.");
        }

        var engines = root.TryGetProperty("engines", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;

        return new WorkerHealth(
            State(root, "process"),
            State(root, "ipc"),
            State(root, "protocol"),
            State(engines, "ocr"),
            State(engines, "languageDetection"),
            State(engines, "translation"),
            State(root, "models"),
            Text(root, "workerVersion") ?? "unknown",
            ParseProtocolVersion(Text(root, "protocolVersion")),
            Text(root, "python"),
            root.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var p) ? p : null);
    }

    /// <summary>The session token echoed by the worker in its <c>worker.initialize.result</c>, or <see langword="null"/>.</summary>
    public static string? ParseSessionToken(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } root ? Text(root, "sessionToken") : null;

    public static ProtocolVersion? ParseInitializeProtocolVersion(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } root && ProtocolVersion.TryParse(Text(root, "protocolVersion"), out var version)
            ? version
            : null;

    private static WorkerComponentState State(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object ? WorkerComponentStateExtensions.ParseWire(Text(parent, name)) : WorkerComponentState.Unknown;

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ProtocolVersion ParseProtocolVersion(string? text) =>
        ProtocolVersion.TryParse(text, out var version) ? version : ProtocolVersion.Current;
}
