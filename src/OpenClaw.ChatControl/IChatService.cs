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

    /// <summary>Fired when a streaming text delta arrives for an active run.</summary>
    event EventHandler<ChatStreamDelta>? DeltaReceived;

    /// <summary>Fired when a run lifecycle event occurs (start, end, error).</summary>
    event EventHandler<ChatLifecycleEvent>? LifecycleChanged;
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
}

public enum ChatLifecyclePhase
{
    Start,
    End,
    Error
}
