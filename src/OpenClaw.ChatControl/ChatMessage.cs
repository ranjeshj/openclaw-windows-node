using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace OpenClaw.ChatControl;

/// <summary>
/// A single chat message. Identity fields (Id, Role, Timestamp) are set once;
/// mutable state (Content, Status, IsStreaming) changes during the message lifecycle.
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public ChatMessage(string id, MessageRole role, string content = "", DateTimeOffset? timestamp = null)
    {
        Id = id;
        Role = role;
        Content = content;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
        Status = MessageStatus.Complete;
    }

    /// <summary>Stable message identifier.</summary>
    public string Id { get; }

    /// <summary>Who sent this message.</summary>
    public MessageRole Role { get; }

    /// <summary>When the message was created.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Formatted timestamp for display (e.g. "8:20 PM").</summary>
    public string FormattedTime => Timestamp.ToLocalTime().ToString("h:mm tt");

    /// <summary>The text content of the message. Updated during streaming via AppendDelta.</summary>
    [ObservableProperty]
    public partial string Content { get; set; }

    /// <summary>Current status in the message lifecycle.</summary>
    [ObservableProperty]
    public partial MessageStatus Status { get; set; }

    /// <summary>True while the message is actively receiving streaming deltas.</summary>
    [ObservableProperty]
    public partial bool IsStreaming { get; set; }

    /// <summary>Run ID associated with this message (for assistant messages during a run).</summary>
    public string? RunId { get; set; }

    // Efficient delta accumulation during streaming
    private readonly StringBuilder _contentBuffer = new();

    /// <summary>Append a streaming delta to this message's content.</summary>
    public void AppendDelta(string delta)
    {
        _contentBuffer.Append(delta);
        Content = _contentBuffer.ToString();
    }

    /// <summary>Finalize the message after streaming completes.</summary>
    public void FinalizeContent()
    {
        if (_contentBuffer.Length > 0)
        {
            Content = _contentBuffer.ToString();
        }
        IsStreaming = false;
        Status = MessageStatus.Complete;
    }

    /// <summary>Mark the message as errored.</summary>
    public void MarkError(string? errorMessage = null)
    {
        IsStreaming = false;
        Status = MessageStatus.Error;
        if (errorMessage != null)
        {
            _contentBuffer.Append($"\n\n⚠️ {errorMessage}");
            Content = _contentBuffer.ToString();
        }
    }

    /// <summary>Prepare the content buffer for streaming (call before first AppendDelta).</summary>
    public void BeginStreaming(string runId)
    {
        RunId = runId;
        _contentBuffer.Clear();
        Content = "";
        IsStreaming = true;
        Status = MessageStatus.Streaming;
    }

    // --- Helpers for XAML x:Bind visibility converters ---

    /// <summary>Whether the thinking indicator should show.</summary>
    public Visibility IsThinkingOrEmpty(MessageStatus status, string content)
        => (status == MessageStatus.Thinking || (status == MessageStatus.Streaming && string.IsNullOrEmpty(content)))
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether content text should show.</summary>
    public Visibility HasContent(string content)
        => string.IsNullOrEmpty(content) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Whether error/aborted indicator should show.</summary>
    public Visibility IsErrorOrAborted(MessageStatus status)
        => (status == MessageStatus.Error || status == MessageStatus.Aborted)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Human-readable status label for error/aborted.</summary>
    public string StatusLabel(MessageStatus status) => status switch
    {
        MessageStatus.Error => "Error",
        MessageStatus.Aborted => "Stopped",
        _ => ""
    };
}

public enum MessageRole
{
    User,
    Assistant,
    System
}

public enum MessageStatus
{
    /// <summary>Message is being sent to the server.</summary>
    Sending,

    /// <summary>Waiting for the agent to start producing output.</summary>
    Thinking,

    /// <summary>Actively receiving streaming deltas.</summary>
    Streaming,

    /// <summary>Message is finalized.</summary>
    Complete,

    /// <summary>An error occurred.</summary>
    Error,

    /// <summary>The run was aborted by the user.</summary>
    Aborted
}
