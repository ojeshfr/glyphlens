using System.Text.Json;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

public class WorkerHealthParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string TruthfulHealth = """
        {"process":"ready","ipc":"connected","protocol":"initialized",
         "engines":{"ocr":"not_implemented","languageDetection":"not_implemented","translation":"not_implemented"},
         "models":"not_installed","workerVersion":"0.2.0","protocolVersion":"1.0","python":"3.12.10","pid":4242}
        """;

    [Fact]
    public void ParseHealth_ReadsEveryField()
    {
        var health = WorkerHealthParser.ParseHealth(Json(TruthfulHealth));

        Assert.Equal(WorkerComponentState.Ready, health.Process);
        Assert.Equal(WorkerComponentState.Connected, health.Ipc);
        Assert.Equal(WorkerComponentState.Initialized, health.Protocol);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Ocr);
        Assert.Equal(WorkerComponentState.NotImplemented, health.LanguageDetection);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Translation);
        Assert.Equal(WorkerComponentState.NotInstalled, health.Models);
        Assert.Equal("0.2.0", health.WorkerVersion);
        Assert.Equal("1.0", health.ProtocolVersion.ToString());
        Assert.Equal("3.12.10", health.PythonVersion);
        Assert.Equal(4242, health.ProcessId);
    }

    [Fact]
    public void UnrecognisedOrMissingStates_BecomeUnknown_NeverReady()
    {
        var health = WorkerHealthParser.ParseHealth(Json("""{"process":"turbo","engines":{"ocr":"ready-ish"},"models":42}"""));

        Assert.Equal(WorkerComponentState.Unknown, health.Process);
        Assert.Equal(WorkerComponentState.Unknown, health.Ocr);
        Assert.Equal(WorkerComponentState.Unknown, health.LanguageDetection);
        Assert.Equal(WorkerComponentState.Unknown, health.Models);
        Assert.Equal("unknown", health.WorkerVersion);
        Assert.Null(health.ProcessId);
    }

    [Fact]
    public void UnknownFieldsAreIgnored_ForForwardCompatibility()
    {
        var health = WorkerHealthParser.ParseHealth(Json("""{"process":"ready","gpu":"cuda:0","engines":{"ocr":"ready","vision":"x"}}"""));

        Assert.Equal(WorkerComponentState.Ready, health.Ocr);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"ready\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void NonObjectPayloads_AreProtocolViolations(string json)
    {
        var ex = Assert.Throws<WorkerException>(() => WorkerHealthParser.ParseHealth(Json(json)));
        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
    }

    [Fact]
    public void MissingPayload_IsAProtocolViolation()
    {
        Assert.Throws<WorkerException>(() => WorkerHealthParser.ParseHealth(null));
    }

    [Fact]
    public void ParseSessionToken_ReadsOnlyAStringToken()
    {
        Assert.Equal("abc", WorkerHealthParser.ParseSessionToken(Json("""{"sessionToken":"abc"}""")));
        Assert.Null(WorkerHealthParser.ParseSessionToken(Json("""{"sessionToken":5}""")));
        Assert.Null(WorkerHealthParser.ParseSessionToken(Json("""{}""")));
        Assert.Null(WorkerHealthParser.ParseSessionToken(null));
    }

    [Fact]
    public void ParseInitializeProtocolVersion_ReadsAValidVersionOnly()
    {
        Assert.Equal("1.3", WorkerHealthParser.ParseInitializeProtocolVersion(Json("""{"protocolVersion":"1.3"}"""))?.ToString());
        Assert.Null(WorkerHealthParser.ParseInitializeProtocolVersion(Json("""{"protocolVersion":"one"}""")));
        Assert.Null(WorkerHealthParser.ParseInitializeProtocolVersion(Json("""{}""")));
    }

    [Theory]
    [InlineData(WorkerComponentState.NotImplemented, "not_implemented", "NOT_IMPLEMENTED")]
    [InlineData(WorkerComponentState.NotInstalled, "not_installed", "NOT_INSTALLED")]
    [InlineData(WorkerComponentState.Ready, "ready", "READY")]
    [InlineData(WorkerComponentState.Connected, "connected", "CONNECTED")]
    [InlineData(WorkerComponentState.Initialized, "initialized", "INITIALIZED")]
    [InlineData(WorkerComponentState.Unknown, "unknown", "UNKNOWN")]
    public void ComponentStates_RoundTripAndDisplayInUpperSnakeCase(WorkerComponentState state, string wire, string display)
    {
        Assert.Equal(wire, state.ToWireString());
        Assert.Equal(display, state.ToDisplayString());
        if (state != WorkerComponentState.Unknown)
        {
            Assert.Equal(state, WorkerComponentStateExtensions.ParseWire(wire));
        }
    }

    [Fact]
    public void EveryWireValueParsesBackToItsOwnState()
    {
        foreach (var state in Enum.GetValues<WorkerComponentState>().Where(s => s != WorkerComponentState.Unknown))
        {
            Assert.Equal(state, WorkerComponentStateExtensions.ParseWire(state.ToWireString()));
        }
    }
}
