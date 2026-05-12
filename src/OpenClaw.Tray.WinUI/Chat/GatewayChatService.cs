using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.ChatControl;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Implements IChatService by wrapping an IOperatorGatewayClient.
/// Maps gateway AgentEventReceived to IChatService streaming events.
/// Thread contract: events fire on background threads; ChatViewModel handles UI marshaling.
/// </summary>
public sealed class GatewayChatService : IChatService, IDisposable
{
    private readonly IOperatorGatewayClient _client;
    private int _lastAgentSeq;
    private bool _disposed;

    public GatewayChatService(IOperatorGatewayClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.AgentEventReceived += OnAgentEvent;
        _client.StatusChanged += OnClientStatusChanged;
        _client.SessionsUpdated += OnSessionsUpdated;
    }

    public event EventHandler<ChatStreamDelta>? DeltaReceived;
    public event EventHandler<ChatLifecycleEvent>? LifecycleChanged;
    public event EventHandler<ChatToolCallEvent>? ToolCallReceived;
    public event EventHandler<ChatReasoningEvent>? ReasoningReceived;
    public event EventHandler<ChatStatusEvent>? StatusReceived;
    public event EventHandler<ChatInjectEvent>? MessageInjected;

    public bool IsConnected { get; private set; } = true;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler? Reconnected;

    private void OnClientStatusChanged(object? sender, ConnectionStatus status)
    {
        var wasConnected = IsConnected;
        IsConnected = status == ConnectionStatus.Connected;
        ConnectionStateChanged?.Invoke(this, IsConnected);

        if (!wasConnected && IsConnected)
        {
            Reconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.CheckHealthAsync();
            return true;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(CancellationToken ct = default)
    {
        try
        {
            var history = await _client.RequestChatHistoryAsync(ct: ct);
            var messages = new List<ChatMessage>();

            foreach (var msg in history)
            {
                var role = msg.Role switch
                {
                    "user" => MessageRole.User,
                    "assistant" => MessageRole.Assistant,
                    _ => MessageRole.System
                };

                var chatMsg = new ChatMessage(
                    id: $"hist-{msg.Ts}",
                    role: role,
                    content: role == MessageRole.User ? UserMessageSanitizer.Sanitize(msg.Content) : msg.Content,
                    timestamp: msg.Timestamp);

                if (role == MessageRole.Assistant)
                    chatMsg.SenderLabel = "Assistant";

                messages.Add(chatMsg);
            }

            return messages;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[GatewayChatService] LoadHistory failed: {ex.Message}");
            return Array.Empty<ChatMessage>();
        }
    }

    public async Task<string> SendAsync(string text, string idempotencyKey, CancellationToken ct = default)
    {
        await _client.SendChatMessageAsync(text);
        // The gateway doesn't return a run ID from chat.send directly.
        // The run ID arrives via AgentEventReceived lifecycle/job start.
        // Return the idempotency key as a correlation ID; the ViewModel
        // associates the actual run ID when the first agent event arrives.
        return idempotencyKey;
    }

    public async Task AbortAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            await _client.AbortRunAsync(runId, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[GatewayChatService] Abort failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ChatSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.RequestSessionsAsync();
            // Return cached sessions (updated via SessionsUpdated event)
            return _cachedSessions;
        }
        catch { return Array.Empty<ChatSessionInfo>(); }
    }

    public async Task SwitchSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        // Gateway uses sessions.messages.subscribe to switch active session
        await _client.SendChatMessageAsync("/new", sessionKey);
    }

    public async Task ResetSessionAsync(string? sessionKey, CancellationToken ct = default)
    {
        await _client.ResetSessionAsync(sessionKey ?? "main");
    }

    public async Task CompactSessionAsync(string? sessionKey, CancellationToken ct = default)
    {
        await _client.CompactSessionAsync(sessionKey ?? "main");
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.RequestModelsListAsync();
            return Array.Empty<string>(); // populated async via ModelsListUpdated
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task SetModelAsync(string model, CancellationToken ct = default)
    {
        await _client.PatchSessionAsync("main", model: model);
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        _cachedSessions = sessions.Select(s => new ChatSessionInfo(
            s.Key ?? "",
            s.DisplayName ?? s.Key ?? "Session",
            s.UpdatedAt)).ToArray();
    }

    private ChatSessionInfo[] _cachedSessions = Array.Empty<ChatSessionInfo>();

    private void OnAgentEvent(object? sender, AgentEventInfo evt)
    {
        if (_disposed) return;

        // Only process events for the main session (ignore cross-session events)
        if (!string.IsNullOrEmpty(evt.SessionKey) &&
            !evt.SessionKey.Contains("main", StringComparison.OrdinalIgnoreCase))
            return;

        // Detect sequence gaps and force history reload
        if (evt.Seq > 0)
        {
            if (_lastAgentSeq > 0 && evt.Seq != _lastAgentSeq + 1)
            {
                _lastAgentSeq = evt.Seq;
                Reconnected?.Invoke(this, EventArgs.Empty);
                return;
            }
            _lastAgentSeq = evt.Seq;
        }

        try
        {
            switch (evt.Stream.ToLowerInvariant())
            {
                case "assistant":
                    // Check if this is reasoning content
                    var isReasoning = evt.Data.ValueKind == JsonValueKind.Object &&
                        evt.Data.TryGetProperty("isReasoning", out var reasoningProp) &&
                        reasoningProp.ValueKind == JsonValueKind.True;

                    if (isReasoning)
                    {
                        var reasoningDelta = ExtractDeltaText(evt);
                        if (!string.IsNullOrEmpty(reasoningDelta))
                        {
                            ReasoningReceived?.Invoke(this, new ChatReasoningEvent
                            {
                                RunId = evt.RunId,
                                Delta = reasoningDelta
                            });
                        }
                    }
                    else
                    {
                        var delta = ExtractDeltaText(evt);
                        if (!string.IsNullOrEmpty(delta))
                        {
                            DeltaReceived?.Invoke(this, new ChatStreamDelta
                            {
                                RunId = evt.RunId,
                                Delta = delta
                            });
                        }
                    }
                    break;

                case "lifecycle":
                    var phase = ExtractLifecyclePhase(evt);
                    if (phase != null)
                    {
                        LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                        {
                            RunId = evt.RunId,
                            Phase = phase.Value,
                            ErrorMessage = phase == ChatLifecyclePhase.Error ? ExtractErrorMessage(evt) : null,
                            Model = ExtractStringField(evt.Data, "model"),
                            InputTokens = ExtractIntField(evt.Data, "inputTokens"),
                            OutputTokens = ExtractIntField(evt.Data, "outputTokens"),
                            ContextPercent = ExtractIntField(evt.Data, "contextPercent"),
                        });

                        if (phase == ChatLifecyclePhase.Error)
                        {
                            StatusReceived?.Invoke(this, new ChatStatusEvent
                            {
                                RunId = evt.RunId,
                                Text = ExtractErrorMessage(evt) ?? "Agent error",
                                Tone = ChatTone.Error
                            });
                        }
                    }
                    break;

                case "job":
                    var jobPhase = ExtractJobPhase(evt);
                    if (jobPhase != null)
                    {
                        LifecycleChanged?.Invoke(this, new ChatLifecycleEvent
                        {
                            RunId = evt.RunId,
                            Phase = jobPhase.Value,
                            ErrorMessage = jobPhase == ChatLifecyclePhase.Error ? ExtractErrorMessage(evt) : null
                        });
                    }
                    break;

                case "tool":
                    var toolEvent = ExtractToolCallEvent(evt);
                    if (toolEvent != null)
                    {
                        ToolCallReceived?.Invoke(this, toolEvent);
                    }
                    break;

                case "error":
                    var errorText = evt.Data.ValueKind == JsonValueKind.Object && evt.Data.TryGetProperty("reason", out var reason)
                        ? reason.GetString() ?? "Unknown error"
                        : evt.Summary ?? "Stream error";
                    StatusReceived?.Invoke(this, new ChatStatusEvent
                    {
                        RunId = evt.RunId,
                        Text = errorText,
                        Tone = ChatTone.Error
                    });
                    break;

                case "item":
                    // Item events carry structured content items (e.g., input audio, response items).
                    // Log for now; no UI rendering needed yet.
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[GatewayChatService] Error processing agent event: {ex.Message}");
        }
    }

    private static DateTimeOffset TimestampFromEvent(AgentEventInfo evt)
        => evt.Ts > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)evt.Ts) : DateTimeOffset.UtcNow;

    private static string? ExtractDeltaText(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind == JsonValueKind.Object)
        {
            if (evt.Data.TryGetProperty("delta", out var delta))
                return delta.GetString();
            if (evt.Data.TryGetProperty("text", out var text))
                return text.GetString();
        }
        return evt.Summary;
    }

    private static ChatLifecyclePhase? ExtractLifecyclePhase(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind == JsonValueKind.Object &&
            evt.Data.TryGetProperty("phase", out var phase))
        {
            return phase.GetString()?.ToLowerInvariant() switch
            {
                "start" => ChatLifecyclePhase.Start,
                "end" => ChatLifecyclePhase.End,
                "error" => ChatLifecyclePhase.Error,
                _ => null
            };
        }
        return null;
    }

    private static ChatLifecyclePhase? ExtractJobPhase(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind == JsonValueKind.Object &&
            evt.Data.TryGetProperty("state", out var state))
        {
            return state.GetString()?.ToLowerInvariant() switch
            {
                "running" => ChatLifecyclePhase.Start,
                "final" or "done" or "completed" => ChatLifecyclePhase.End,
                "error" or "failed" => ChatLifecyclePhase.Error,
                _ => null
            };
        }
        return null;
    }

    private static string? ExtractErrorMessage(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind == JsonValueKind.Object)
        {
            if (evt.Data.TryGetProperty("error", out var error))
                return error.GetString();
            if (evt.Data.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        return "Agent run failed";
    }

    private static ChatToolCallEvent? ExtractToolCallEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return null;

        var name = evt.Data.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var toolCallId = evt.Data.TryGetProperty("toolCallId", out var tcIdProp) ? tcIdProp.GetString() : null;
        var phaseStr = evt.Data.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() : null;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(phaseStr))
            return null;

        var phase = phaseStr.ToLowerInvariant() switch
        {
            "start" => ToolCallPhase.Running,
            "result" or "done" => ToolCallPhase.Done,
            "error" => ToolCallPhase.Error,
            _ => (ToolCallPhase?)null
        };

        if (phase == null)
            return null;

        // Extract tool args as pretty-printed JSON
        string? argsJson = null;
        if (phase == ToolCallPhase.Running && evt.Data.TryGetProperty("args", out var args))
        {
            try
            {
                argsJson = System.Text.Json.JsonSerializer.Serialize(args,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch { argsJson = args.ToString(); }
        }

        // Extract full tool output
        string? toolOutput = null;
        string? resultSummary = null;
        if (phase == ToolCallPhase.Done && evt.Data.TryGetProperty("result", out var result))
        {
            toolOutput = ExtractToolFullOutput(result);
            resultSummary = TruncateSummary(toolOutput);
        }

        // Extract structured details from result
        string? details = null;
        if (phase == ToolCallPhase.Done && evt.Data.TryGetProperty("result", out var resultForDetails))
        {
            if (resultForDetails.TryGetProperty("details", out var detailsProp))
            {
                try { details = System.Text.Json.JsonSerializer.Serialize(detailsProp, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
                catch { details = detailsProp.ToString(); }
            }
        }

        return new ChatToolCallEvent
        {
            RunId = evt.RunId,
            ToolCallId = toolCallId,
            ToolName = name,
            Phase = phase.Value,
            ResultSummary = resultSummary,
            ArgsJson = argsJson,
            ToolOutput = toolOutput,
            Details = details
        };
    }

    private static string? ExtractToolFullOutput(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String)
            return result.GetString();

        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(text.GetString());
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    private static string? ExtractToolResultSummary(JsonElement result)
    {
        return TruncateSummary(ExtractToolFullOutput(result));
    }

    private static string? TruncateSummary(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    private static string? ExtractStringField(JsonElement data, string fieldName)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(fieldName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int? ExtractIntField(JsonElement data, string fieldName)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(fieldName, out var prop) ||
            prop.ValueKind != JsonValueKind.Number)
            return null;

        // Handle fractional values (85.0) and large numbers safely
        if (prop.TryGetInt32(out var intVal))
            return intVal;
        try { return (int)prop.GetDouble(); } catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.AgentEventReceived -= OnAgentEvent;
        _client.StatusChanged -= OnClientStatusChanged;
        _client.SessionsUpdated -= OnSessionsUpdated;
    }
}
