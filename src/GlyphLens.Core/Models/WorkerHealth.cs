using GlyphLens.Core.Contracts;

namespace GlyphLens.Core.Models;

/// <summary>
/// State of one worker component, as reported by the worker. The wire form is lower snake case
/// (<c>not_implemented</c>); <see cref="WorkerComponentStateExtensions.ToDisplayString"/> gives <c>NOT_IMPLEMENTED</c>.
/// </summary>
public enum WorkerComponentState
{
    /// <summary>The worker did not report this component, or reported a value this build does not know.</summary>
    Unknown = 0,

    /// <summary>The worker process is up and serving requests.</summary>
    Ready,

    /// <summary>The pipe to the application is connected.</summary>
    Connected,

    /// <summary>The pipe is not connected.</summary>
    Disconnected,

    /// <summary>The protocol handshake (<c>worker.initialize</c>) completed.</summary>
    Initialized,

    /// <summary>The handshake has not happened.</summary>
    NotInitialized,

    /// <summary>The component has no implementation in this build.</summary>
    NotImplemented,

    /// <summary>The component is implemented but its models are not installed.</summary>
    NotInstalled,

    /// <summary>The component failed to load.</summary>
    Unavailable,
}

public static class WorkerComponentStateExtensions
{
    /// <summary>The wire representation, e.g. <c>not_implemented</c>.</summary>
    public static string ToWireString(this WorkerComponentState state) => state switch
    {
        WorkerComponentState.Ready => "ready",
        WorkerComponentState.Connected => "connected",
        WorkerComponentState.Disconnected => "disconnected",
        WorkerComponentState.Initialized => "initialized",
        WorkerComponentState.NotInitialized => "not_initialized",
        WorkerComponentState.NotImplemented => "not_implemented",
        WorkerComponentState.NotInstalled => "not_installed",
        WorkerComponentState.Unavailable => "unavailable",
        _ => "unknown",
    };

    /// <summary>Upper snake case for display, e.g. <c>NOT_IMPLEMENTED</c>.</summary>
    public static string ToDisplayString(this WorkerComponentState state) =>
        state.ToWireString().ToUpperInvariant();

    /// <summary>Parses a wire value; anything unrecognised becomes <see cref="WorkerComponentState.Unknown"/>.</summary>
    public static WorkerComponentState ParseWire(string? value) => value switch
    {
        "ready" => WorkerComponentState.Ready,
        "connected" => WorkerComponentState.Connected,
        "disconnected" => WorkerComponentState.Disconnected,
        "initialized" => WorkerComponentState.Initialized,
        "not_initialized" => WorkerComponentState.NotInitialized,
        "not_implemented" => WorkerComponentState.NotImplemented,
        "not_installed" => WorkerComponentState.NotInstalled,
        "unavailable" => WorkerComponentState.Unavailable,
        _ => WorkerComponentState.Unknown,
    };
}

/// <summary>
/// What the worker reports about itself in response to <c>worker.health</c>.
/// </summary>
/// <remarks>
/// Each field is a distinct fact: a live process says nothing about whether an engine is usable. An engine is only
/// <see cref="WorkerComponentState.Ready"/> when it can actually do its job.
/// </remarks>
public sealed record WorkerHealth(
    WorkerComponentState Process,
    WorkerComponentState Ipc,
    WorkerComponentState Protocol,
    WorkerComponentState Ocr,
    WorkerComponentState LanguageDetection,
    WorkerComponentState Translation,
    WorkerComponentState Models,
    string WorkerVersion,
    ProtocolVersion ProtocolVersion,
    string? PythonVersion,
    int? ProcessId);
