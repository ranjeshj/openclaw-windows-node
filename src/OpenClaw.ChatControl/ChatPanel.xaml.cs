using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

public sealed partial class ChatPanel : UserControl
{
    private bool _autoScrollEnabled = true;
    private bool _emptyStateHidden;
    private ChatMessage? _trackedStreamingMessage;
    private bool _scrollPending;
    private int _programmaticScrollGen;
    private int _lastSeenScrollGen;
    private DispatcherQueueTimer? _scrollThrottleTimer;

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

    private void OnNewChatRequested(object sender, EventArgs e)
    {
        // Send /new through the normal send flow
        _autoScrollEnabled = true;
        ScrollToBottomButton.Visibility = Visibility.Collapsed;
        ViewModel?.SendCommand.Execute("/new");
    }

    private void OnCopyLastResponseRequested(object sender, EventArgs e)
    {
        if (ViewModel?.Messages == null) return;

        // Find the last assistant message
        for (int i = ViewModel.Messages.Count - 1; i >= 0; i--)
        {
            if (ViewModel.Messages[i].Role == MessageRole.Assistant &&
                !string.IsNullOrEmpty(ViewModel.Messages[i].Content))
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(ViewModel.Messages[i].Content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                break;
            }
        }
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
    /// Request a scroll-to-bottom. During streaming, requests are coalesced
    /// via a 100ms throttle timer so we scroll only after layout settles.
    /// </summary>
    private void RequestScrollToBottom()
    {
        if (_scrollPending) return;
        _scrollPending = true;

        // Use a timer to let the layout pass complete before reading ScrollableHeight.
        // This avoids jitter from ChangeView firing before ItemsRepeater finishes layout.
        EnsureScrollTimer();
        if (!_scrollThrottleTimer!.IsRunning)
            _scrollThrottleTimer.Start();
    }

    private void EnsureScrollTimer()
    {
        if (_scrollThrottleTimer != null) return;
        _scrollThrottleTimer = DispatcherQueue.CreateTimer();
        _scrollThrottleTimer.Interval = TimeSpan.FromMilliseconds(100);
        _scrollThrottleTimer.IsRepeating = false;
        _scrollThrottleTimer.Tick += OnScrollThrottleTick;
    }

    private void OnScrollThrottleTick(DispatcherQueueTimer sender, object args)
    {
        _scrollPending = false;
        if (_autoScrollEnabled)
            ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        _programmaticScrollGen++;

        // Try BringIntoView on the last realized element — this lets WinUI
        // compute the correct scroll offset after layout, avoiding stale ScrollableHeight.
        var count = ViewModel?.Messages.Count ?? 0;
        if (count > 0)
        {
            var lastElement = MessageList.TryGetElement(count - 1);
            if (lastElement is UIElement el)
            {
                el.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 1.0, // align bottom
                });
                return;
            }
        }

        // Fallback: direct ChangeView (element not realized or no messages)
        MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MessageScrollViewer.ViewChanged += OnScrollViewChanged;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Ignore events from our own programmatic scrolls (generation-based tracking).
        // _lastSeenScrollGen catches up to _programmaticScrollGen as ViewChanged fires.
        if (_lastSeenScrollGen < _programmaticScrollGen)
        {
            if (!e.IsIntermediate)
                _lastSeenScrollGen = _programmaticScrollGen;
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
