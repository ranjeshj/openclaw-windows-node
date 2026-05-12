using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenClaw.ChatControl;

/// <summary>
/// Session orchestrator: manages the message list, sends user messages,
/// handles streaming events, and coordinates UI state.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IChatService _service;
    private readonly Action<Action> _dispatchToUI;
    private readonly object _streamLock = new();
    private CancellationTokenSource? _sessionCts;
    private ChatMessage? _activeStreamingMessage;
    private System.Threading.Timer? _pendingRunTimer;
    private bool _disposed;

    /// <summary>
    /// Create a new ChatViewModel.
    /// </summary>
    /// <param name="service">The chat backend service.</param>
    /// <param name="dispatchToUI">
    /// Marshals an action to the UI thread. For WinUI: <c>dispatcherQueue.TryEnqueue</c>.
    /// For tests: <c>action => action()</c> (synchronous execution).
    /// </param>
    public ChatViewModel(IChatService service, Action<Action> dispatchToUI)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatchToUI = dispatchToUI ?? throw new ArgumentNullException(nameof(dispatchToUI));
        _sessionCts = new CancellationTokenSource();

        IsConnected = _service.IsConnected;

        _service.DeltaReceived += OnDeltaReceived;
        _service.LifecycleChanged += OnLifecycleChanged;
        _service.ToolCallReceived += OnToolCallReceived;
        _service.ReasoningReceived += OnReasoningReceived;
        _service.StatusReceived += OnStatusReceived;
        _service.ConnectionStateChanged += OnConnectionStateChanged;
        _service.Reconnected += OnReconnected;
    }

    /// <summary>All messages in the current session.</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>Whether an agent run is currently active.</summary>
    [ObservableProperty]
    public partial bool IsRunActive { get; private set; }

    /// <summary>The run ID of the currently active run, if any.</summary>
    [ObservableProperty]
    public partial string? ActiveRunId { get; private set; }

    /// <summary>Error message for display, if any.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether history has been loaded.</summary>
    [ObservableProperty]
    public partial bool IsHistoryLoaded { get; private set; }

    /// <summary>Whether the chat service is connected to the backend.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; private set; } = true;

    /// <summary>Pending run timeout in milliseconds. Default 120 000 ms (2 minutes).</summary>
    internal int PendingRunTimeoutMs { get; set; } = 120_000;

    /// <summary>Load conversation history from the backend.</summary>
    public async Task LoadHistoryAsync()
    {
        if (_disposed) return;

        try
        {
            ErrorMessage = null;
            var history = await _service.LoadHistoryAsync(_sessionCts?.Token ?? default);

            _dispatchToUI(() =>
            {
                // Don't clear messages if a send/run started while history was loading
                if (IsRunActive) return;

                Messages.Clear();
                foreach (var msg in history)
                {
                    Messages.Add(msg);
                }
                IsHistoryLoaded = true;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dispatchToUI(() => ErrorMessage = $"Failed to load history: {ex.Message}");
        }
    }

    /// <summary>Send a user message.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed) return;

        ErrorMessage = null;
        var trimmed = text.Trim();

        // Handle /new as a session reset: send to gateway, clear, reload
        if (trimmed.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            await HandleNewSessionAsync(trimmed);
            return;
        }

        // Add user message to the list immediately
        var userMsg = new ChatMessage(
            id: Guid.NewGuid().ToString("N"),
            role: MessageRole.User,
            content: trimmed);
        userMsg.Status = MessageStatus.Sending;

        _dispatchToUI(() => Messages.Add(userMsg));

        try
        {
            var idempotencyKey = Guid.NewGuid().ToString("N");
            var runId = await _service.SendAsync(trimmed, idempotencyKey, _sessionCts?.Token ?? default);

            _dispatchToUI(() =>
            {
                userMsg.Status = MessageStatus.Complete;

                // Create the assistant placeholder for streaming
                var assistantMsg = new ChatMessage(
                    id: Guid.NewGuid().ToString("N"),
                    role: MessageRole.Assistant);
                assistantMsg.Status = MessageStatus.Thinking;

                lock (_streamLock)
                {
                    ActiveRunId = runId;
                    IsRunActive = true;
                    _activeStreamingMessage = assistantMsg;
                }

                StartPendingRunTimer();
                Messages.Add(assistantMsg);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dispatchToUI(() =>
            {
                userMsg.Status = MessageStatus.Error;
                ErrorMessage = $"Failed to send: {ex.Message}";
            });
        }
    }

    private async Task HandleNewSessionAsync(string command)
    {
        try
        {
            // Send /new to gateway — it creates a new session
            var idempotencyKey = Guid.NewGuid().ToString("N");
            await _service.SendAsync(command, idempotencyKey, _sessionCts?.Token ?? default);

            // Clear local state
            _dispatchToUI(() =>
            {
                lock (_streamLock)
                {
                    _activeStreamingMessage = null;
                    ActiveRunId = null;
                    IsRunActive = false;
                }
                Messages.Clear();
                IsHistoryLoaded = false;
            });

            // Reload history (should be empty for new session)
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dispatchToUI(() => ErrorMessage = $"Failed to start new session: {ex.Message}");
        }
    }

    private bool CanSend(string? text) => !string.IsNullOrWhiteSpace(text) && !IsRunActive;

    /// <summary>Abort the active agent run.</summary>
    [RelayCommand(CanExecute = nameof(IsRunActive))]
    private async Task AbortAsync()
    {
        if (_disposed) return;

        CancelPendingRunTimer();

        string? runIdToAbort;
        lock (_streamLock)
        {
            // Use the real RunId from the streaming message if available,
            // otherwise fall back to ActiveRunId (which may be the idempotency key).
            runIdToAbort = _activeStreamingMessage?.RunId ?? ActiveRunId;
        }

        if (runIdToAbort == null) return;

        try
        {
            await _service.AbortAsync(runIdToAbort, _sessionCts?.Token ?? default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dispatchToUI(() => ErrorMessage = $"Failed to abort: {ex.Message}");
        }

        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                if (_activeStreamingMessage != null)
                {
                    _activeStreamingMessage.IsStreaming = false;
                    _activeStreamingMessage.Status = MessageStatus.Aborted;
                    _activeStreamingMessage = null;
                }
                ActiveRunId = null;
                IsRunActive = false;
            }
        });
    }

    /// <summary>
    /// Check if an incoming event's RunId matches the active streaming message.
    /// Accepts the event if: RunId matches directly, or ActiveRunId matches,
    /// or the message is still in Thinking state (not yet associated with a RunId).
    /// </summary>
    private bool IsActiveRunEvent(string eventRunId)
    {
        if (_activeStreamingMessage == null) return false;
        if (_activeStreamingMessage.RunId == eventRunId) return true;
        if (ActiveRunId == eventRunId) return true;
        // Accept if message is waiting for RunId association (Thinking state)
        if (_activeStreamingMessage.Status == MessageStatus.Thinking && _activeStreamingMessage.RunId == null)
        {
            _activeStreamingMessage.RunId = eventRunId;
            return true;
        }
        return false;
    }

    private void OnDeltaReceived(object? sender, ChatStreamDelta e)
    {
        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                if (!IsActiveRunEvent(e.RunId))
                    return;

                if (_activeStreamingMessage!.Status == MessageStatus.Thinking)
                {
                    _activeStreamingMessage.BeginStreaming(e.RunId);
                }

                _activeStreamingMessage.AppendDelta(e.Delta);
            }
        });
    }

    private void OnLifecycleChanged(object? sender, ChatLifecycleEvent e)
    {
        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                switch (e.Phase)
                {
                    case ChatLifecyclePhase.Start:
                        if (_activeStreamingMessage != null && _activeStreamingMessage.RunId == e.RunId)
                        {
                            // Already set up
                        }
                        else if (_activeStreamingMessage != null && _activeStreamingMessage.Status == MessageStatus.Thinking)
                        {
                            _activeStreamingMessage.RunId = e.RunId;
                        }
                        // Capture model name from lifecycle start
                        if (_activeStreamingMessage != null && !string.IsNullOrEmpty(e.Model))
                        {
                            _activeStreamingMessage.ModelName = e.Model;
                        }
                        if (_activeStreamingMessage != null && string.IsNullOrEmpty(_activeStreamingMessage.SenderLabel))
                        {
                            _activeStreamingMessage.SenderLabel = "Assistant";
                        }
                        break;

                    case ChatLifecyclePhase.End:
                        if (IsActiveRunEvent(e.RunId))
                        {
                            CancelPendingRunTimer();
                            if (e.InputTokens.HasValue) _activeStreamingMessage!.InputTokens = e.InputTokens;
                            if (e.OutputTokens.HasValue) _activeStreamingMessage!.OutputTokens = e.OutputTokens;
                            if (e.ContextPercent.HasValue) _activeStreamingMessage!.ContextPercent = e.ContextPercent;
                            if (!string.IsNullOrEmpty(e.Model)) _activeStreamingMessage!.ModelName = e.Model;

                            _activeStreamingMessage!.FinalizeContent();
                            _activeStreamingMessage = null;
                            ActiveRunId = null;
                            IsRunActive = false;
                        }
                        break;

                    case ChatLifecyclePhase.Error:
                        if (IsActiveRunEvent(e.RunId))
                        {
                            CancelPendingRunTimer();
                            _activeStreamingMessage!.MarkError(e.ErrorMessage);
                            _activeStreamingMessage = null;
                            ActiveRunId = null;
                            IsRunActive = false;
                        }
                        break;
                }
            }
        });
    }

    private void OnToolCallReceived(object? sender, ChatToolCallEvent e)
    {
        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                if (!IsActiveRunEvent(e.RunId) || _activeStreamingMessage == null)
                    return;

                if (e.Phase == ToolCallPhase.Running)
                {
                    var tc = new ToolCallInfo(e.ToolCallId, e.ToolName);
                    if (!string.IsNullOrEmpty(e.ArgsJson)) tc.ArgsJson = e.ArgsJson;
                    _activeStreamingMessage.ToolCalls.Add(tc);
                }
                else
                {
                    foreach (var tc in _activeStreamingMessage.ToolCalls)
                    {
                        if (tc.ToolCallId == e.ToolCallId)
                        {
                            tc.Phase = e.Phase;
                            tc.ResultSummary = e.ResultSummary;
                            if (!string.IsNullOrEmpty(e.ToolOutput)) tc.ToolOutput = e.ToolOutput;
                            if (!string.IsNullOrEmpty(e.Details)) tc.Details = e.Details;
                            break;
                        }
                    }
                }
            }
        });
    }

    private void OnReasoningReceived(object? sender, ChatReasoningEvent e)
    {
        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                if (!IsActiveRunEvent(e.RunId) || _activeStreamingMessage == null)
                    return;

                _activeStreamingMessage.AppendReasoning(e.Delta);
            }
        });
    }

    private void OnStatusReceived(object? sender, ChatStatusEvent e)
    {
        _dispatchToUI(() =>
        {
            var statusMsg = new ChatMessage(
                id: Guid.NewGuid().ToString("N"),
                role: MessageRole.Status,
                content: e.Text);
            statusMsg.Tone = e.Tone;
            Messages.Add(statusMsg);
        });
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        _dispatchToUI(() => IsConnected = connected);
    }

    private void OnReconnected(object? sender, EventArgs e)
    {
        _dispatchToUI(() =>
        {
            // Clear any active streaming state so the UI is clean before reload
            lock (_streamLock)
            {
                if (_activeStreamingMessage != null)
                {
                    _activeStreamingMessage.IsStreaming = false;
                    _activeStreamingMessage = null;
                }
                ActiveRunId = null;
                IsRunActive = false;
            }
            CancelPendingRunTimer();
        });

        // Reload history to pick up any messages that arrived while disconnected
        _ = LoadHistoryAsync();
    }

    private void StartPendingRunTimer()
    {
        CancelPendingRunTimer();
        _pendingRunTimer = new System.Threading.Timer(
            _ => _dispatchToUI(OnPendingRunTimedOut),
            null,
            PendingRunTimeoutMs,
            Timeout.Infinite);
    }

    private void CancelPendingRunTimer()
    {
        _pendingRunTimer?.Dispose();
        _pendingRunTimer = null;
    }

    private void OnPendingRunTimedOut()
    {
        lock (_streamLock)
        {
            if (!IsRunActive) return;

            if (_activeStreamingMessage != null)
            {
                _activeStreamingMessage.MarkError("Timed out waiting for a reply");
                _activeStreamingMessage = null;
            }
            ActiveRunId = null;
            IsRunActive = false;
            ErrorMessage = "Timed out waiting for a reply";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _service.DeltaReceived -= OnDeltaReceived;
        _service.LifecycleChanged -= OnLifecycleChanged;
        _service.ToolCallReceived -= OnToolCallReceived;
        _service.ReasoningReceived -= OnReasoningReceived;
        _service.StatusReceived -= OnStatusReceived;
        _service.ConnectionStateChanged -= OnConnectionStateChanged;
        _service.Reconnected -= OnReconnected;

        CancelPendingRunTimer();

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
    }
}
