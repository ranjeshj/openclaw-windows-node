using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Renders a single tool call as a compact expandable card.
/// Collapsed: "⏳ tool_name" (running) or "✅ tool_name" (done).
/// Expanded: shows result summary text.
/// </summary>
public sealed partial class ToolCallCard : UserControl
{
    private ToolCallInfo? _toolCall;
    private bool _isExpanded;

    public ToolCallCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_toolCall != null)
            _toolCall.PropertyChanged -= OnToolCallPropertyChanged;

        _toolCall = args.NewValue as ToolCallInfo;

        if (_toolCall != null)
        {
            _toolCall.PropertyChanged += OnToolCallPropertyChanged;
            UpdateVisualState();
        }
    }

    private void OnToolCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (_toolCall == null) return;

        ToolNameText.Text = _toolCall.DisplayLabel;
        StatusIcon.Text = _toolCall.StatusIcon;

        RunningSpinner.Visibility = _toolCall.Phase == ToolCallPhase.Running
            ? Visibility.Visible : Visibility.Collapsed;

        // Show expand button only when there's a result to show
        ExpandButton.Visibility = !string.IsNullOrEmpty(_toolCall.ResultSummary)
            ? Visibility.Visible : Visibility.Collapsed;

        // Hide spinner when not running
        if (_toolCall.Phase != ToolCallPhase.Running)
            RunningSpinner.Visibility = Visibility.Collapsed;
    }

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;

        if (_isExpanded && _toolCall?.ResultSummary != null)
        {
            ResultText.Text = _toolCall.ResultSummary;
            ResultText.Visibility = Visibility.Visible;
            ExpandIcon.Glyph = "\uE70D"; // ChevronDown
        }
        else
        {
            ResultText.Visibility = Visibility.Collapsed;
            ExpandIcon.Glyph = "\uE76C"; // ChevronRight
        }
    }
}
