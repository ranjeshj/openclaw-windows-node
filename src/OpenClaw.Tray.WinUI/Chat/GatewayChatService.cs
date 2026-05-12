using System;
using System.Collections.Generic;
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
    private bool _disposed;

    public GatewayChatService(IOperatorGatewayClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.AgentEventReceived += OnAgentEvent;
    }

    public event EventHandler<ChatStreamDelta>? DeltaReceived;
    public event EventHandler<ChatLifecycleEvent>? LifecycleChanged;
    public event EventHandler<ChatToolCallEvent>? ToolCallReceived;

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

                messages.Add(new ChatMessage(
                    id: $"hist-{msg.Ts}",
                    role: role,
                    content: msg.Content,
                    timestamp: msg.Timestamp));
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

    private void OnAgentEvent(object? sender, AgentEventInfo evt)
    {
        if (_disposed) return;

        try
        {
            switch (evt.Stream.ToLowerInvariant())
            {
                case "assistant":
                    var delta = ExtractDeltaText(evt);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        DeltaReceived?.Invoke(this, new ChatStreamDelta
                        {
                            RunId = evt.RunId,
                            Delta = delta
                        });
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
                            ErrorMessage = phase == ChatLifecyclePhase.Error ? ExtractErrorMessage(evt) : null
                        });
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
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[GatewayChatService] Error processing agent event: {ex.Message}");
        }
    }

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

        string? resultSummary = null;
        if (phase == ToolCallPhase.Done && evt.Data.TryGetProperty("result", out var result))
        {
            resultSummary = ExtractToolResultSummary(result);
        }

        return new ChatToolCallEvent
        {
            RunId = evt.RunId,
            ToolCallId = toolCallId,
            ToolName = name,
            Phase = phase.Value,
            ResultSummary = resultSummary
        };
    }

    private static string? ExtractToolResultSummary(JsonElement result)
    {
        // Result can be { content: [{ type: "text", text: "..." }] } or a string
        if (result.ValueKind == JsonValueKind.String)
            return TruncateSummary(result.GetString());

        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    return TruncateSummary(text.GetString());
            }
        }

        return null;
    }

    private static string? TruncateSummary(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.AgentEventReceived -= OnAgentEvent;
    }
}
