using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

public sealed partial class ChatPanel : UserControl
{
    private bool _autoScrollEnabled = true;
    private bool _emptyStateHidden;
    private ChatMessage? _trackedStreamingMessage;
    private bool _scrollPending;
    private bool _isProgrammaticScroll;

    /// <summary>Fired when the user clicks the close button in the header.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Fired when the user clicks the pop-out button in the header.</summary>
    public event EventHandler? PopoutRequested;

    public ChatPanel()
    {
        InitializeComponent();
    }

    public ChatViewModel? ViewModel
    {
        get => (ChatViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ChatViewModel), typeof(ChatPanel),
            new PropertyMetadata(null, OnViewModelChanged));

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (ChatPanel)d;
        var oldVm = e.OldValue as ChatViewModel;
        var newVm = e.NewValue as ChatViewModel;

        if (oldVm != null)
        {
            oldVm.Messages.CollectionChanged -= panel.OnMessagesChanged;
            oldVm.PropertyChanged -= panel.OnViewModelPropertyChanged;
        }

        if (newVm != null)
        {
            newVm.Messages.CollectionChanged += panel.OnMessagesChanged;
            newVm.PropertyChanged += panel.OnViewModelPropertyChanged;
            panel.UpdateEmptyState();
            panel.UpdateInputState();
            panel.UpdateErrorBar();
        }
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            TrackLastMessageForStreaming();

            if (_autoScrollEnabled)
                RequestScrollToBottom();
            else
                ScrollToBottomButton.Visibility = Visibility.Visible;
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            UntrackStreamingMessage();
            _autoScrollEnabled = true;
            RequestScrollToBottom();
        }
    }

    private void TrackLastMessageForStreaming()
    {
        UntrackStreamingMessage();

        if (ViewModel?.Messages.Count > 0)
        {
            var lastMsg = ViewModel.Messages[^1];
            if (lastMsg.Role == MessageRole.Assistant)
            {
                _trackedStreamingMessage = lastMsg;
                _trackedStreamingMessage.PropertyChanged += OnStreamingMessagePropertyChanged;
            }
        }
    }

    private void UntrackStreamingMessage()
    {
        if (_trackedStreamingMessage != null)
        {
            _trackedStreamingMessage.PropertyChanged -= OnStreamingMessagePropertyChanged;
            _trackedStreamingMessage = null;
        }
    }

    private void OnStreamingMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessage.Content) && _autoScrollEnabled)
        {
            RequestScrollToBottom();
        }

        if (e.PropertyName == nameof(ChatMessage.IsStreaming) && sender is ChatMessage msg && !msg.IsStreaming)
        {
            UntrackStreamingMessage();
            if (_autoScrollEnabled)
                RequestScrollToBottom();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsRunActive):
                UpdateInputState();
                break;
            case nameof(ChatViewModel.ErrorMessage):
                UpdateErrorBar();
                break;
        }
    }

    private void UpdateEmptyState()
    {
        var hasMessages = ViewModel?.Messages.Count > 0;
        if (hasMessages && !_emptyStateHidden)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            _emptyStateHidden = true;
        }
        else if (!hasMessages)
        {
            EmptyState.Visibility = Visibility.Visible;
            _emptyStateHidden = false;
        }
    }

    private void UpdateInputState()
    {
        if (ViewModel == null) return;
        InputBox.IsRunActive = ViewModel.IsRunActive;
    }

    private void UpdateErrorBar()
    {
        if (ViewModel?.ErrorMessage is { } error)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
        }
        else
        {
            ErrorBar.IsOpen = false;
        }
    }

    private void OnSendRequested(object sender, string text)
    {
        _autoScrollEnabled = true;
        ScrollToBottomButton.Visibility = Visibility.Collapsed;
        ViewModel?.SendCommand.Execute(text);
        InputBox.ClearInput();
    }

    private void OnAbortRequested(object sender, EventArgs e)
    {
        ViewModel?.AbortCommand.Execute(null);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPopoutClick(object sender, RoutedEventArgs e)
    {
        PopoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrollToBottomClick(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = true;
        ScrollToBottomButton.Visibility = Visibility.Collapsed;
        ScrollToBottom();
    }

    /// <summary>
    /// Coalesce rapid scroll requests into a single deferred scroll.
    /// Prevents jitter from many streaming deltas queuing independent scrolls.
    /// </summary>
    private void RequestScrollToBottom()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollPending = false;
            if (_autoScrollEnabled)
                ScrollToBottom();
        });
    }

    private void ScrollToBottom()
    {
        _isProgrammaticScroll = true;
        MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MessageScrollViewer.ViewChanged += OnScrollViewChanged;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Ignore events from our own programmatic scrolls
        if (_isProgrammaticScroll)
        {
            if (!e.IsIntermediate)
                _isProgrammaticScroll = false;
            return;
        }

        if (e.IsIntermediate) return;

        var atBottom = MessageScrollViewer.VerticalOffset >= MessageScrollViewer.ScrollableHeight - 40;

        if (atBottom)
        {
            _autoScrollEnabled = true;
            ScrollToBottomButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _autoScrollEnabled = false;
            if (ViewModel?.Messages.Count > 0)
                ScrollToBottomButton.Visibility = Visibility.Visible;
        }
    }
}
