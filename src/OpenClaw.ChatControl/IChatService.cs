using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.ChatControl;

/// <summary>
/// Single interface for chat backend communication.
/// Implementations: MockChatService (dev), GatewayChatService (production).
/// Thread contract: events may fire on any thread; consumers must marshal to UI.
/// </summary>
public interface IChatService
{
    /// <summary>Load conversation history for the current session.</summary>
    Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Send a user message. Returns the run ID for abort tracking.
    /// </summary>
    Task<string> SendAsync(string text, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Abort an active agent run.</summary>
    Task AbortAsync(string runId, CancellationToken ct = default);

    // ─── Session Management ───

    /// <summary>List available chat sessions.</summary>
    Task<IReadOnlyList<ChatSessionInfo>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>Switch to a different chat session.</summary>
    Task SwitchSessionAsync(string sessionKey, CancellationToken ct = default);

    /// <summary>Reset a chat session (clear history). Null = current session.</summary>
    Task ResetSessionAsync(string? sessionKey = null, CancellationToken ct = default);

    /// <summary>Compact a chat session (summarize older messages). Null = current session.</summary>
    Task CompactSessionAsync(string? sessionKey = null, CancellationToken ct = default);

    // ─── Model Selection ───

    /// <summary>List available AI models.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Set the active model for the current session.</summary>
    Task SetModelAsync(string model, CancellationToken ct = default);

    /// <summary>Fired when a streaming text delta arrives for an active run.</summary>
    event EventHandler<ChatStreamDelta>? DeltaReceived;

    /// <summary>Fired when a run lifecycle event occurs (start, end, error).</summary>
    event EventHandler<ChatLifecycleEvent>? LifecycleChanged;

    /// <summary>Fired when a tool call event arrives (start, result, error).</summary>
    event EventHandler<ChatToolCallEvent>? ToolCallReceived;

    /// <summary>Fired when reasoning/thinking text arrives from the model.</summary>
    event EventHandler<ChatReasoningEvent>? ReasoningReceived;

    /// <summary>Fired when a status event occurs (info, warning, error notifications).</summary>
    event EventHandler<ChatStatusEvent>? StatusReceived;

    /// <summary>Check if the backend is reachable.</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>Fired when the service injects a message (e.g. server-pushed notes).</summary>
    event EventHandler<ChatInjectEvent>? MessageInjected;

    /// <summary>Whether the service is currently connected to the backend.</summary>
    bool IsConnected { get; }

    /// <summary>Fired when the connection state changes. Payload is true when connected.</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Fired when the service transitions from disconnected to connected.
    /// Consumers should refresh state (e.g. reload history).
    /// </summary>
    event EventHandler? Reconnected;
}

/// <summary>Payload for a message injected by the service.</summary>
public sealed class ChatInjectEvent : EventArgs
{
    public required string Text { get; init; }
    public MessageRole Role { get; init; } = MessageRole.System;
}

/// <summary>A streaming text delta for an active agent run.</summary>
public sealed class ChatStreamDelta : EventArgs
{
    public required string RunId { get; init; }
    public required string Delta { get; init; }
}

/// <summary>A run lifecycle event (start, end, error).</summary>
public sealed class ChatLifecycleEvent : EventArgs
{
    public required string RunId { get; init; }
    public required ChatLifecyclePhase Phase { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? ContextPercent { get; init; }
}

public enum ChatLifecyclePhase
{
    Start,
    End,
    Error
}

/// <summary>A tool call event within an agent run.</summary>
public sealed class ChatToolCallEvent : EventArgs
{
    public required string RunId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required ToolCallPhase Phase { get; init; }
    public string? ResultSummary { get; init; }
    public string? ArgsJson { get; init; }
    public string? ToolOutput { get; init; }
    public string? Details { get; init; }
}

/// <summary>A reasoning/thinking text delta from the model.</summary>
public sealed class ChatReasoningEvent : EventArgs
{
    public required string RunId { get; init; }
    public required string Delta { get; init; }
    public bool IsFinal { get; init; }
}

/// <summary>A status notification event.</summary>
public sealed class ChatStatusEvent : EventArgs
{
    public required string RunId { get; init; }
    public required string Text { get; init; }
    public required ChatTone Tone { get; init; }
}
