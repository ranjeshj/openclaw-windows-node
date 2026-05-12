using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Renders an assistant message with two-phase rendering:
/// - During streaming: plain TextBlock (fast, no re-parse overhead)
/// - After completion: rendered markdown via MarkdownRenderer (Markdig → RichTextBlock)
/// </summary>
public sealed partial class AssistantMessageControl : UserControl
{
    private ChatMessage? _message;
    private bool _markdownRendered;

    public AssistantMessageControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Unsubscribe from previous
        if (_message != null)
        {
            _message.PropertyChanged -= OnMessagePropertyChanged;
        }

        _message = args.NewValue as ChatMessage;
        _markdownRendered = false;

        if (_message != null)
        {
            _message.PropertyChanged += OnMessagePropertyChanged;
            UpdateVisualState();
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatMessage.Content):
                if (_message?.IsStreaming == true)
                {
                    // During streaming: update plain text only
                    StreamingText.Text = _message.Content;
                }
                break;

            case nameof(ChatMessage.Status):
            case nameof(ChatMessage.IsStreaming):
                UpdateVisualState();
                break;
        }
    }

    private void UpdateVisualState()
    {
        if (_message == null) return;

        // Reset all panels
        ThinkingPanel.Visibility = Visibility.Collapsed;
        StreamingText.Visibility = Visibility.Collapsed;
        RenderedContent.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;

        switch (_message.Status)
        {
            case MessageStatus.Thinking:
                ThinkingPanel.Visibility = Visibility.Visible;
                break;

            case MessageStatus.Streaming:
                StreamingText.Text = _message.Content;
                StreamingText.Visibility = Visibility.Visible;
                break;

            case MessageStatus.Complete:
                RenderMarkdown();
                break;

            case MessageStatus.Error:
                // Show partial content + error
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    RenderMarkdown();
                }
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Error";
                break;

            case MessageStatus.Aborted:
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    RenderMarkdown();
                }
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Stopped";
                break;

            default:
                // Sending or other — show content as text
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    StreamingText.Text = _message.Content;
                    StreamingText.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    private void RenderMarkdown()
    {
        if (_message == null || string.IsNullOrEmpty(_message.Content)) return;

        // Only render once per content finalization
        if (!_markdownRendered)
        {
            var rendered = MarkdownRenderer.Render(_message.Content);
            RenderedContent.Content = rendered;
            _markdownRendered = true;
        }
        RenderedContent.Visibility = Visibility.Visible;
    }
}
