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
}
