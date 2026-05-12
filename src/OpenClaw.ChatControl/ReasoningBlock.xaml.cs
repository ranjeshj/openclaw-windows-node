using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Collapsible reasoning/thinking block. Shows a dimmed one-line preview
/// when collapsed and the full reasoning text when expanded.
/// </summary>
public sealed partial class ReasoningBlock : UserControl
{
    private bool _isExpanded;

    public ReasoningBlock()
    {
        InitializeComponent();
    }

    /// <summary>The reasoning text content to display.</summary>
    public string ReasoningText
    {
        get => (string)GetValue(ReasoningTextProperty);
        set => SetValue(ReasoningTextProperty, value);
    }

    public static readonly DependencyProperty ReasoningTextProperty =
        DependencyProperty.Register(nameof(ReasoningText), typeof(string), typeof(ReasoningBlock),
            new PropertyMetadata("", OnReasoningTextChanged));

    private static void OnReasoningTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (ReasoningBlock)d;
        var text = e.NewValue as string ?? "";
        block.PreviewText.Text = text;
        block.FullText.Text = text;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        if (_isExpanded)
        {
            PreviewText.Visibility = Visibility.Collapsed;
            FullText.Visibility = Visibility.Visible;
            ToggleIcon.Glyph = "\uE70D"; // ChevronDown
        }
        else
        {
            PreviewText.Visibility = Visibility.Visible;
            FullText.Visibility = Visibility.Collapsed;
            ToggleIcon.Glyph = "\uE76C"; // ChevronRight
        }
    }
}
