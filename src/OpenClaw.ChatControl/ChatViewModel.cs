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

        _service.DeltaReceived += OnDeltaReceived;
        _service.LifecycleChanged += OnLifecycleChanged;
        _service.ToolCallReceived += OnToolCallReceived;
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
        if (_disposed || ActiveRunId == null) return;

        var runId = ActiveRunId;
        try
        {
            await _service.AbortAsync(runId, _sessionCts?.Token ?? default);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dispatchToUI(() => ErrorMessage = $"Failed to abort: {ex.Message}");
        }

        // Finalize the streaming message as aborted
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

    private void OnDeltaReceived(object? sender, ChatStreamDelta e)
    {
        _dispatchToUI(() =>
        {
            lock (_streamLock)
            {
                if (_activeStreamingMessage == null || _activeStreamingMessage.RunId != e.RunId)
                    return;

                if (_activeStreamingMessage.Status == MessageStatus.Thinking)
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
                        // If we already have a streaming message for this run, update it
                        if (_activeStreamingMessage != null && _activeStreamingMessage.RunId == e.RunId)
                        {
                            // Already set up — this is a no-op
                        }
                        else if (_activeStreamingMessage != null && _activeStreamingMessage.Status == MessageStatus.Thinking)
                        {
                            // Associate the run ID with the waiting message
                            _activeStreamingMessage.RunId = e.RunId;
                        }
                        break;

                    case ChatLifecyclePhase.End:
                        if (_activeStreamingMessage != null &&
                            (_activeStreamingMessage.RunId == e.RunId || ActiveRunId == e.RunId))
                        {
                            _activeStreamingMessage.FinalizeContent();
                            _activeStreamingMessage = null;
                            ActiveRunId = null;
                            IsRunActive = false;
                        }
                        break;

                    case ChatLifecyclePhase.Error:
                        if (_activeStreamingMessage != null &&
                            (_activeStreamingMessage.RunId == e.RunId || ActiveRunId == e.RunId))
                        {
                            _activeStreamingMessage.MarkError(e.ErrorMessage);
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
                if (_activeStreamingMessage == null ||
                    (_activeStreamingMessage.RunId != e.RunId && ActiveRunId != e.RunId))
                    return;

                if (e.Phase == ToolCallPhase.Running)
                {
                    _activeStreamingMessage.ToolCalls.Add(new ToolCallInfo(e.ToolCallId, e.ToolName));
                }
                else
                {
                    // Find existing tool call and update it
                    foreach (var tc in _activeStreamingMessage.ToolCalls)
                    {
                        if (tc.ToolCallId == e.ToolCallId)
                        {
                            tc.Phase = e.Phase;
                            tc.ResultSummary = e.ResultSummary;
                            break;
                        }
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _service.DeltaReceived -= OnDeltaReceived;
        _service.LifecycleChanged -= OnLifecycleChanged;
        _service.ToolCallReceived -= OnToolCallReceived;

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
    }
}
