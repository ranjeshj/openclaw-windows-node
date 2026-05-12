using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace OpenClaw.ChatControl;

public sealed partial class ChatInputBox : UserControl
{
    public ChatInputBox()
    {
        InitializeComponent();
    }

    /// <summary>Fired when the user requests to send a message.</summary>
    public event EventHandler<string>? SendRequested;

    /// <summary>Fired when the user requests to abort the active run.</summary>
    public event EventHandler? AbortRequested;

    /// <summary>Fired when the user clicks "New Chat" in the more menu.</summary>
    public event EventHandler? NewChatRequested;

    /// <summary>Fired when the user clicks "Copy Last Response" in the more menu.</summary>
    public event EventHandler? CopyLastResponseRequested;

    /// <summary>Fired when the user clicks "Reset Session" in the more menu.</summary>
    public event EventHandler? ResetSessionRequested;

    /// <summary>Fired when the user clicks "Compact Session" in the more menu.</summary>
    public event EventHandler? CompactSessionRequested;

    /// <summary>
    /// Whether an agent run is currently active.
    /// Toggles between Send and Stop button visibility.
    /// </summary>
    public bool IsRunActive
    {
        get => _isRunActive;
        set
        {
            _isRunActive = value;
            SendButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;

            if (!value)
            {
                // Re-evaluate send button enabled state
                UpdateSendEnabled();
                InputTextBox.Focus(FocusState.Programmatic);
            }
        }
    }
    private bool _isRunActive;

    /// <summary>
    /// The label displayed in the footer area (e.g. "OpenClaw Native Chat" or with model name).
    /// </summary>
    public string FooterLabel
    {
        get => _footerLabel;
        set
        {
            _footerLabel = value;
            FooterText.Text = value;
        }
    }
    private string _footerLabel = "OpenClaw Native Chat";

    /// <summary>Clear the input text box.</summary>
    public void ClearInput()
    {
        InputTextBox.Text = "";
        UpdateSendEnabled();
    }

    /// <summary>Focus the input text box.</summary>
    public void FocusInput()
    {
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            var isShiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!isShiftDown && !_isRunActive)
            {
                // Prevent the TextBox from inserting a newline
                e.Handled = true;
                TrySend();
            }
            // Shift+Enter: don't handle — TextBox inserts newline via AcceptsReturn
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        TrySend();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        AbortRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendEnabled();
    }

    private void TrySend()
    {
        var text = InputTextBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            SendRequested?.Invoke(this, text);
        }
    }

    private void UpdateSendEnabled()
    {
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(InputTextBox.Text) && !_isRunActive;
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        NewChatRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyLastResponseClick(object sender, RoutedEventArgs e)
    {
        CopyLastResponseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnResetSessionClick(object sender, RoutedEventArgs e)
    {
        ResetSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCompactSessionClick(object sender, RoutedEventArgs e)
    {
        CompactSessionRequested?.Invoke(this, EventArgs.Empty);
    }
}
