using OpenClaw.Shared;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding;
using OpenClawTray.Services.Connection;
using OpenClawTray.Windows;
using System;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Services;

/// <summary>
/// Owns creation, lifecycle, and display logic for all application windows
/// (Hub, Chat, Voice overlay, QuickSend, ConnectionStatus, Onboarding).
/// Extracted from App.xaml.cs to reduce its size and isolate window management.
/// </summary>
internal sealed class WindowManager
{
    private readonly AppState _appModel;
    private readonly SettingsManager? _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Func<GatewayConnectionManager?> _connectionManagerProvider;
    private readonly Func<GatewayRegistry?> _gatewayRegistryProvider;
    private readonly Func<NodeService?> _nodeServiceProvider;
    private readonly NodeCoordinator? _nodeCoordinator;
    private readonly Action _updateTrayIcon;
    private readonly string _identityDataPath;

    // Callbacks for operations that stay in App
    private readonly Func<(bool ok, string url, string token, string source, bool isBootstrap)> _resolveChatCredentials;
    private readonly Func<Task> _checkForUpdatesAsync;
    private readonly ToastService? _toastService;

    // Windows (created on demand)
    private HubWindow? _hubWindow;
    private ChatWindow? _chatWindow;
    private VoiceOverlayWindow? _voiceOverlayWindow;
    private QuickSendDialog? _quickSendDialog;
    private ConnectionStatusWindow? _connectionStatusWindow;
    private VoiceService? _standaloneVoiceService;
    private OnboardingWindow? _onboardingWindow;

    /// <summary>Re-raised from HubWindow.SettingsSaved so App can subscribe.</summary>
    public event EventHandler? SettingsSaved;

    /// <summary>
    /// Single dispatch callback routed to TrayActionDispatcher.
    /// Set by App.xaml.cs after creating both WindowManager and TrayActionDispatcher.
    /// </summary>
    public Action<string>? DispatchAction { get; set; }

    // Expose window references for App.xaml.cs code that still reads them directly
    public HubWindow? HubWindow => _hubWindow;
    public ChatWindow? ChatWindow => _chatWindow;
    public VoiceOverlayWindow? VoiceOverlayWindow => _voiceOverlayWindow;
    public OnboardingWindow? OnboardingWindow => _onboardingWindow;
    public QuickSendDialog? QuickSendDialog => _quickSendDialog;
    public ConnectionStatusWindow? ConnectionStatusWindow => _connectionStatusWindow;
    public VoiceService? StandaloneVoiceService => _standaloneVoiceService;

    public WindowManager(
        AppState appModel,
        SettingsManager? settings,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        Func<GatewayConnectionManager?> connectionManagerProvider,
        Func<GatewayRegistry?> gatewayRegistryProvider,
        Func<NodeService?> nodeServiceProvider,
        NodeCoordinator? nodeCoordinator,
        Action updateTrayIcon,
        string identityDataPath,
        Func<(bool ok, string url, string token, string source, bool isBootstrap)> resolveChatCredentials,
        Func<Task> checkForUpdatesAsync,
        ToastService? toastService)
    {
        _appModel = appModel;
        _settings = settings;
        _dispatcher = dispatcher;
        _connectionManagerProvider = connectionManagerProvider;
        _gatewayRegistryProvider = gatewayRegistryProvider;
        _nodeServiceProvider = nodeServiceProvider;
        _nodeCoordinator = nodeCoordinator;
        _updateTrayIcon = updateTrayIcon;
        _identityDataPath = identityDataPath;
        _resolveChatCredentials = resolveChatCredentials;
        _checkForUpdatesAsync = checkForUpdatesAsync;
        _toastService = toastService;
    }

    /// <summary>
    /// Pre-warm the chat window so WebView2 init happens early (1-3s).
    /// Called from App.xaml.cs after credential resolution.
    /// </summary>
    public void PrewarmChatWindow(string url, string token)
    {
        _chatWindow = new ChatWindow(url, token);
    }

    #region ShowHub

    internal void ShowHub(string? navigateTo = null, bool activate = true)
    {
        var connectionManager = _connectionManagerProvider();
        var gatewayRegistry = _gatewayRegistryProvider();
        var nodeService = _nodeServiceProvider();

        if (_hubWindow == null || _hubWindow.IsClosed)
        {
            _hubWindow = new HubWindow();
            _hubWindow.AppModel = _appModel;
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = connectionManager?.OperatorClient;
            _hubWindow.CurrentStatus = _appModel.Status;
            _hubWindow.DispatchAction = DispatchAction;
            _hubWindow.QuickSendAction = () => ShowQuickSend();
            _hubWindow.ConnectionManager = connectionManager;
            _hubWindow.GatewayRegistry = gatewayRegistry;
            _hubWindow.ClearAppAgentEventsCache = () => _appModel.ClearAgentEvents();
            if (nodeService != null)
            {
                _hubWindow.NodeIsConnected = nodeService.IsConnected;
                _hubWindow.NodeIsPaired = nodeService.IsPaired;
                _hubWindow.NodeIsPendingApproval = nodeService.IsPendingApproval;
                _hubWindow.NodeShortDeviceId = nodeService.ShortDeviceId;
                _hubWindow.NodeFullDeviceId = nodeService.FullDeviceId;
            }
            _hubWindow.VoiceServiceInstance = nodeService?.VoiceService ?? _standaloneVoiceService;
            _hubWindow.SettingsSaved += OnHubSettingsSaved;
            _hubWindow.Closed += (s, e) =>
            {
                _hubWindow.SettingsSaved -= OnHubSettingsSaved;
                _hubWindow = null;
            };

            // Seed ALL cached data BEFORE first navigation so pages see data in Initialize()
            SeedHubCachedData();

            // Navigate to default page now that properties and data are set
            _hubWindow.NavigateToDefault();
        }
        // Always update live state
        if (_hubWindow.AppModel == null)
            _hubWindow.AppModel = _appModel;
        _hubWindow.Settings = _settings;
        _hubWindow.GatewayClient = connectionManager?.OperatorClient;
        _hubWindow.CurrentStatus = _appModel.Status;
        _hubWindow.VoiceServiceInstance = nodeService?.VoiceService ?? _standaloneVoiceService;
        if (nodeService != null)
        {
            _hubWindow.NodeIsConnected = nodeService.IsConnected;
            _hubWindow.NodeIsPaired = nodeService.IsPaired;
            _hubWindow.NodeIsPendingApproval = nodeService.IsPendingApproval;
            _hubWindow.NodeShortDeviceId = nodeService.ShortDeviceId;
            _hubWindow.NodeFullDeviceId = nodeService.FullDeviceId;
        }

        // Seed cached data into hub (also on re-show)
        SeedHubCachedData();

        if (navigateTo != null)
        {
            _hubWindow.NavigateTo(navigateTo);
        }
        if (activate)
        {
            _hubWindow.Activate();
        }
        else
        {
            // Show without stealing focus — used by right-click on the
            // tray icon where the popup needs to remain the foreground
            // window (popups light-dismiss if focus moves away).
            // If the Hub was minimized, restore it first so it actually
            // becomes visible behind the popup; otherwise Show(false)
            // is a no-op on a minimized window.
            try
            {
                if (_hubWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op
                    && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    op.Restore(activateWindow: false);
                }
                _hubWindow.AppWindow.Show(activateWindow: false);
            }
            catch { /* swallow */ }
        }
    }

    private void OnHubSettingsSaved(object? sender, EventArgs e)
    {
        SettingsSaved?.Invoke(sender, e);
    }

    internal void SeedHubCachedData()
    {
        if (_hubWindow == null) return;
        // Agent events need explicit seeding since they use append semantics
        if (_appModel.AgentEvents.Count > 0) _hubWindow.SeedAgentEvents(_appModel.AgentEvents);
    }

    #endregion

    #region Chat / WebChat

    internal void ShowChatWindow()
    {
        if (_settings == null) return;
        var creds = _resolveChatCredentials();
        if (!creds.ok)
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway URL or credential is not configured");
            return;
        }

        if (creds.isBootstrap)
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway pairing is not complete");
            return;
        }

        Logger.Info($"[ChatWindow] Quick-chat credentials resolved from {creds.source}");
        if (_chatWindow == null)
        {
            _chatWindow = new ChatWindow(creds.url, creds.token);
        }

        // Bug 2: cached ChatWindow may have been pre-warmed with empty/stale credentials
        // (built before pairing completed). Refresh on every tray click so quick-chat
        // follows the same resolver path as the companion-app operator client.
        _chatWindow.RefreshCredentials(creds.url, creds.token);

        // Toggle: if visible, hide; if hidden, show near tray
        if (_chatWindow.Visible)
        {
            _chatWindow.Hide();
        }
        else
        {
            // Bug 1: When called from the wizard's close handler, OnboardingWindow.Close()
            // steals focus on the same UI tick, deactivating ChatWindow → its
            // OnWindowActivated auto-hides it immediately. Defer the show to a later
            // dispatcher tick (Low priority) so the close + focus-loss cascade settles
            // before we make the chat window visible.
            var window = _chatWindow;
            _dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    try { window.ShowNearTrayAnimated(); }
                    catch (Exception ex) { Logger.Warn($"ShowChatWindow deferred show failed: {ex.Message}"); }
                });
        }
    }

    internal void ShowWebChat()
    {
        if (_settings == null) return;
        var creds = _resolveChatCredentials();
        if (!creds.ok)
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway URL or credential is not configured");
            return;
        }

        if (creds.isBootstrap)
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway pairing is not complete");
            return;
        }

        ShowHub("chat");
    }

    /// <summary>
    /// Force-close and null the chat window. Called by App on settings change
    /// that requires credential refresh.
    /// </summary>
    internal void ResetChatWindow()
    {
        if (_chatWindow != null)
        {
            _chatWindow.ForceClose();
            _chatWindow = null;
        }
    }

    #endregion

    #region Canvas

    internal void ShowCanvasWindow()
    {
        var nodeService = _nodeServiceProvider();

        if (_settings?.NodeCanvasEnabled == false)
        {
            Logger.Warn("[Canvas] Canvas capability is disabled; opening capability settings");
            ShowHub("capabilities");
            return;
        }

        if (nodeService == null)
        {
            ShowConnectionSettingsForPairingIssue(
                "Canvas",
                "Windows node is not initialized");
            return;
        }

        if (nodeService.IsPendingApproval || !nodeService.IsPaired)
        {
            ShowConnectionSettingsForPairingIssue(
                "Canvas",
                "Windows node pairing is not complete");
            return;
        }

        nodeService.ShowCanvasWindow();
    }

    #endregion

    #region Voice Overlay

    internal void ShowVoiceOverlay()
    {
        var nodeService = _nodeServiceProvider();
        var connectionManager = _connectionManagerProvider();
        var voiceService = nodeService?.VoiceService ?? EnsureStandaloneVoiceService();
        if (voiceService == null)
        {
            // STT not enabled — show settings
            ShowHub("voice");
            return;
        }

        if (_voiceOverlayWindow == null || _voiceOverlayWindow.AppWindow == null)
        {
            _voiceOverlayWindow = new VoiceOverlayWindow(voiceService, new AppLogger());
            _voiceOverlayWindow.Closed += (_, _) => _voiceOverlayWindow = null;
            // Wire transcription to gateway chat when connected
            _voiceOverlayWindow.TextSubmitted += text =>
            {
                var client = connectionManager?.OperatorClient;
                if (client != null && _appModel.Status == ConnectionStatus.Connected)
                {
                    _ = client.SendChatMessageAsync(text);
                }
            };
            // Wire Settings button → open the Hub on the Voice & Audio page.
            _voiceOverlayWindow.SettingsRequested += () =>
            {
                _dispatcher.TryEnqueue(() => ShowHub("voice"));
            };
        }

        _voiceOverlayWindow.Activate();
    }

    internal VoiceService? EnsureStandaloneVoiceService()
    {
        if (_settings?.NodeSttEnabled != true)
            return null;

        return _standaloneVoiceService ??= new VoiceService(new AppLogger(), _settings);
    }

    #endregion

    #region QuickSend

    internal void ShowQuickSend(string? prefillMessage = null)
    {
        var connectionManager = _connectionManagerProvider();
        if (connectionManager?.OperatorClient == null)
        {
            Logger.Warn("QuickSend blocked: gateway client not initialized");
            return;
        }

        try
        {
            // Keep a strong reference to the window; otherwise the dialog can be GC'd
            // and appear to not open (especially when triggered from a hotkey).
            if (_quickSendDialog != null)
            {
                // If caller wants a prefill, re-create to apply it.
                if (!string.IsNullOrEmpty(prefillMessage))
                {
                    try { _quickSendDialog.Close(); } catch { }
                    _quickSendDialog = null;
                }
                else
                {
                    Logger.Info("QuickSend dialog already open; activating");
                    _quickSendDialog.ShowAsync();
                    return;
                }
            }

            Logger.Info("Showing QuickSend dialog");
            // Bug #3: pass a Func that resolves the live OperatorClient on
            // every Send so post-pair / restart / reinit swaps are observed.
            var dialog = new QuickSendDialog(() => connectionManager?.OperatorClient as OpenClawGatewayClient, prefillMessage);
            dialog.Closed += (s, e) =>
            {
                if (ReferenceEquals(_quickSendDialog, dialog))
                {
                    _quickSendDialog = null;
                }
            };
            _quickSendDialog = dialog;
            dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to show QuickSend dialog: {ex.Message}");
        }
    }

    #endregion

    #region ConnectionStatus

    internal void ShowConnectionStatusWindow()
    {
        var connectionManager = _connectionManagerProvider();
        var gatewayRegistry = _gatewayRegistryProvider();

        if (_connectionStatusWindow != null && !_connectionStatusWindow.IsClosed)
        {
            _connectionStatusWindow.Activate();
            return;
        }
        _connectionStatusWindow = new ConnectionStatusWindow(
            connectionManager!.Diagnostics,
            gatewayRegistry,
            connectionManager);
        _connectionStatusWindow.Activate();
    }

    #endregion

    #region Status / Settings / History / Activity

    internal void ShowStatusDetail()
    {
        ShowHub("general");
    }

    internal void ShowSettings()
    {
        ShowHub("settings");
    }

    internal void ShowNotificationHistory()
    {
        ShowActivityStream("notification");
    }

    internal void ShowActivityStream(string? filter = null)
    {
        ShowHub("activity");
        _hubWindow?.SetActivityFilter(filter);
    }

    #endregion

    #region Connection Settings

    internal void ShowConnectionSettingsForPairingIssue(string source, string reason)
    {
        Logger.Warn($"[{source}] {reason}; opening connection settings");
        ShowHub("connection");
    }

    #endregion

    #region Onboarding

    internal async Task ShowOnboardingAsync()
    {
        if (_settings == null) return;

        if (_onboardingWindow != null)
        {
            try { _onboardingWindow.Activate(); return; } catch { _onboardingWindow = null; }
        }

        var connectionManager = _connectionManagerProvider();

        _onboardingWindow = new OnboardingWindow(_settings, _identityDataPath);
        _onboardingWindow.OnboardingCompleted += (s, e) =>
        {
            Logger.Info("Onboarding completed");
            _onboardingWindow = null;

            // If the persistent client was already initialized during onboarding, keep it
            if (connectionManager?.OperatorClient is OpenClawGatewayClient { IsConnectedToGateway: true })
            {
                Logger.Info("Gateway client already connected from onboarding — keeping");
                return;
            }

            // Otherwise reinitialize with saved settings
            _ = connectionManager?.ReconnectAsync();

            // Keep hub window in sync with new client
            if (_hubWindow != null && !_hubWindow.IsClosed)
            {
                _hubWindow.Settings = _settings;
                _hubWindow.GatewayClient = connectionManager?.OperatorClient;
                _hubWindow.CurrentStatus = _appModel.Status;
            }
        };
        _onboardingWindow.Closed += (s, e) => _onboardingWindow = null;
        _onboardingWindow.Activate();
    }

    #endregion

    #region Surface Improvements Tip

    internal void ShowSurfaceImprovementsTipIfNeeded()
    {
        if (_settings == null || _settings.HasSeenActivityStreamTip) return;

        _settings.HasSeenActivityStreamTip = true;
        _settings.Save();

        try
        {
            _toastService?.ShowToast(new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTip"))
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTipDetail"))
                .AddButton(new Microsoft.Toolkit.Uwp.Notifications.ToastButton()
                    .SetContent(LocalizationHelper.GetString("Toast_ActivityStreamTipButton"))
                    .AddArgument("action", "open_activity")));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show activity stream tip: {ex.Message}");
        }
    }

    #endregion

    #region Shutdown helpers

    /// <summary>Dispose standalone voice service during shutdown.</summary>
    internal void DisposeStandaloneVoiceService()
    {
        _standaloneVoiceService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _standaloneVoiceService = null;
    }

    /// <summary>Force-close chat window during shutdown.</summary>
    internal void CloseChatWindow()
    {
        _chatWindow?.ForceClose();
        _chatWindow = null;
    }

    /// <summary>Close quick send dialog during shutdown.</summary>
    internal void CloseQuickSendDialog()
    {
        try { _quickSendDialog?.Close(); } catch { }
        _quickSendDialog = null;
    }

    #endregion
}
