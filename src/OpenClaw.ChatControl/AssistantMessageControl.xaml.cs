using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Renders an assistant message with two-phase rendering:
/// - During streaming: plain TextBlock (fast, no re-parse overhead)
/// - After completion: rendered markdown via MarkdownRenderer (Markdig → RichTextBlock)
/// Also renders inline tool call cards as they arrive.
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
            _message.ToolCalls.CollectionChanged -= OnToolCallsChanged;
        }

        _message = args.NewValue as ChatMessage;
        _markdownRendered = false;

        if (_message != null)
        {
            _message.PropertyChanged += OnMessagePropertyChanged;
            _message.ToolCalls.CollectionChanged += OnToolCallsChanged;
            UpdateToolCallsVisibility();
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
                    StreamingText.Text = _message.Content;
                }
                break;

            case nameof(ChatMessage.Status):
            case nameof(ChatMessage.IsStreaming):
                UpdateVisualState();
                break;

            case nameof(ChatMessage.HasReasoning):
            case nameof(ChatMessage.ReasoningContent):
                UpdateReasoningVisibility();
                break;
        }
    }

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateToolCallsVisibility();
    }

    private void UpdateToolCallsVisibility()
    {
        if (_message == null) return;

        if (_message.ToolCalls.Count > 0)
        {
            ToolCallsList.ItemsSource = _message.ToolCalls;
            ToolCallsList.Visibility = Visibility.Visible;
        }
        else
        {
            ToolCallsList.Visibility = Visibility.Collapsed;
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
        MetadataFooterText.Visibility = Visibility.Collapsed;

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
                ShowMetadataFooter();
                break;

            case MessageStatus.Error:
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    RenderMarkdown();
                }
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Error";
                ShowMetadataFooter();
                break;

            case MessageStatus.Aborted:
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    RenderMarkdown();
                }
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = "Stopped";
                ShowMetadataFooter();
                break;

            default:
                if (!string.IsNullOrEmpty(_message.Content))
                {
                    StreamingText.Text = _message.Content;
                    StreamingText.Visibility = Visibility.Visible;
                }
                break;
        }

        UpdateReasoningVisibility();
    }

    private void UpdateReasoningVisibility()
    {
        if (_message?.HasReasoning == true && !string.IsNullOrEmpty(_message.ReasoningContent))
        {
            ReasoningPanel.ReasoningText = _message.ReasoningContent;
            ReasoningPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ReasoningPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMetadataFooter()
    {
        if (_message == null) return;
        var footer = _message.MetadataFooter;
        if (!string.IsNullOrEmpty(footer))
        {
            MetadataFooterText.Text = footer;
            MetadataFooterText.Visibility = Visibility.Visible;
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
