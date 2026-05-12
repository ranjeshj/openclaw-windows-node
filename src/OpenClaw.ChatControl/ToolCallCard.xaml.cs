using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Renders a single tool call as a compact expandable card.
/// Header: tool-specific icon + capitalized name + status.
/// Two independently expandable sections: args (JSON) and output.
/// </summary>
public sealed partial class ToolCallCard : UserControl
{
    private ToolCallInfo? _toolCall;
    private bool _argsExpanded;
    private bool _outputExpanded;

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
        _argsExpanded = false;
        _outputExpanded = false;

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

        if (_toolCall.Phase != ToolCallPhase.Running)
            RunningSpinner.Visibility = Visibility.Collapsed;

        // Show args expand button when args are available
        ExpandArgsButton.Visibility = !string.IsNullOrEmpty(_toolCall.ArgsJson)
            ? Visibility.Visible : Visibility.Collapsed;

        // Show output expand button when output or result is available
        ExpandOutputButton.Visibility = (!string.IsNullOrEmpty(_toolCall.ToolOutput) || !string.IsNullOrEmpty(_toolCall.ResultSummary))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExpandArgsClick(object sender, RoutedEventArgs e)
    {
        _argsExpanded = !_argsExpanded;
        if (_argsExpanded && _toolCall?.ArgsJson != null)
        {
            ArgsText.Text = _toolCall.ArgsJson;
            ArgsSection.Visibility = Visibility.Visible;
            ExpandArgsIcon.Glyph = "\uE70D"; // ChevronDown
        }
        else
        {
            ArgsSection.Visibility = Visibility.Collapsed;
            ExpandArgsIcon.Glyph = "\uE76C"; // ChevronRight
        }
    }

    private void OnExpandOutputClick(object sender, RoutedEventArgs e)
    {
        _outputExpanded = !_outputExpanded;
        if (_outputExpanded && _toolCall != null)
        {
            OutputText.Text = _toolCall.ToolOutput ?? _toolCall.ResultSummary ?? "";
            OutputSection.Visibility = Visibility.Visible;
            ExpandOutputIcon.Glyph = "\uE70D";
        }
        else
        {
            OutputSection.Visibility = Visibility.Collapsed;
            ExpandOutputIcon.Glyph = "\uE7B8";
        }
    }
}
