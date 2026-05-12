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

    /// <summary>Fired when the user requests a message action (e.g. ReadAloud). 
    /// Copy and Delete are handled internally; ReadAloud is surfaced for consumer wiring.</summary>
    public event EventHandler<ChatMessageActionEventArgs>? MessageActionRequested;

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

    /// <summary>
    /// Controls visibility of tool call trace cards in assistant messages.
    /// When false, tool cards are hidden entirely.
    /// </summary>
    public bool ShowToolTrace
    {
        get => (bool)GetValue(ShowToolTraceProperty);
        set => SetValue(ShowToolTraceProperty, value);
    }

    public static readonly DependencyProperty ShowToolTraceProperty =
        DependencyProperty.Register(nameof(ShowToolTrace), typeof(bool), typeof(ChatPanel),
            new PropertyMetadata(true));

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
            panel.UpdateNoticeBanner();
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

                // Start periodic re-scroll timer during streaming
                EnsureScrollTimer();
                _scrollThrottleTimer!.Start();
            }
        }
    }

    private void UntrackStreamingMessage()
    {
        if (_trackedStreamingMessage != null)
        {
            _trackedStreamingMessage.PropertyChanged -= OnStreamingMessagePropertyChanged;
            _trackedStreamingMessage = null;

            // Stop periodic re-scroll when no longer streaming
            _scrollThrottleTimer?.Stop();
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
                UpdateNoticeBanner();
                break;
            case nameof(ChatViewModel.IsConnected):
                UpdateNoticeBanner();
                break;
            case nameof(ChatViewModel.SelectedModel):
                InputBox.FooterLabel = ViewModel!.SelectedModel != null
                    ? $"OpenClaw Native Chat \u00b7 {ViewModel.SelectedModel}"
                    : "OpenClaw Native Chat";
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

    private void UpdateNoticeBanner()
    {
        if (ViewModel == null)
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            return;
        }

        if (!ViewModel.IsConnected)
        {
            NoticeText.Text = "Disconnected \u2014 check gateway connection";
            NoticeBanner.Visibility = Visibility.Visible;
        }
        else if (ViewModel.ErrorMessage != null)
        {
            NoticeText.Text = ViewModel.ErrorMessage;
            NoticeBanner.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnNoticeRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.LoadHistoryAsync();
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

    private void OnResetSessionRequested(object sender, EventArgs e)
    {
        _autoScrollEnabled = true;
        ScrollToBottomButton.Visibility = Visibility.Collapsed;
        ViewModel?.SendCommand.Execute("/reset");
    }

    private void OnCompactSessionRequested(object sender, EventArgs e)
    {
        ViewModel?.SendCommand.Execute("/compact");
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
    /// Request a scroll-to-bottom. Coalesced via flag — only one scroll
    /// per layout pass. Uses DispatcherQueue at Normal priority to run
    /// after the current layout cycle completes.
    /// </summary>
    private void RequestScrollToBottom()
    {
        if (_scrollPending) return;
        _scrollPending = true;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _scrollPending = false;
            if (_autoScrollEnabled)
                ScrollToBottom();
        });
    }

    private void EnsureScrollTimer()
    {
        // Timer used for periodic re-scroll during streaming
        if (_scrollThrottleTimer != null) return;
        _scrollThrottleTimer = DispatcherQueue.CreateTimer();
        _scrollThrottleTimer.Interval = TimeSpan.FromMilliseconds(150);
        _scrollThrottleTimer.IsRepeating = true;
        _scrollThrottleTimer.Tick += OnScrollThrottleTick;
    }

    private void OnScrollThrottleTick(DispatcherQueueTimer sender, object args)
    {
        // Periodic re-scroll while streaming to catch layout changes
        if (_autoScrollEnabled && _trackedStreamingMessage != null)
            ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        _programmaticScrollGen++;
        MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MessageScrollViewer.ViewChanged += OnScrollViewChanged;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_scrollThrottleTimer != null)
        {
            _scrollThrottleTimer.Stop();
            _scrollThrottleTimer.Tick -= OnScrollThrottleTick;
            _scrollThrottleTimer = null;
        }
        MessageScrollViewer.ViewChanged -= OnScrollViewChanged;
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
