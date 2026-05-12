using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClawTray.Onboarding;
using OpenClawTray.Services.Connection;
using OpenClawTray.Services.LocalGatewaySetup;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Updatum;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using WindowManager = OpenClawTray.Services.WindowManager;

namespace OpenClawTray;

public partial class App : Application
{
    private const string PipeName = "OpenClawTray-DeepLink";
    
    internal static readonly UpdatumManager AppUpdater = new("shanselman", "openclaw-windows-hub")
    {
        FetchOnlyLatestRelease = true,
        InstallUpdateSingleFileExecutableName = "OpenClaw.Tray.WinUI",
    };

    private TrayIcon? _trayIcon;
    private GatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    /// <summary>
    /// Cached reference to the most recently constructed local-setup engine. Used by
    /// <see cref="OnPairingStatusChanged"/> to suppress the "copy pairing command" toast
    /// during Phase 14 auto-pair (Bug #2, manual test 2026-05-05). Null when no local
    /// setup has run in this app lifetime.
    /// </summary>
    private LocalGatewaySetupEngine? _localSetupEngine;
    /// <summary>
    /// When true, the connection manager suppresses node auto-connect after operator handshake.
    /// Set during the WSL local-setup flow so the engine controls node pairing in its own phase.
    /// </summary>
    private volatile bool _suppressNodeDuringSetup;

    /// <summary>The persistent gateway client. Used by the onboarding wizard for RPC calls.</summary>
    public IOperatorGatewayClient? GatewayClient => _connectionManager?.OperatorClient;
    public GatewayRegistry? Registry => _gatewayRegistry;
    public GatewayConnectionManager? ConnectionManager => _connectionManager;
    internal SettingsManager Settings => _settings ?? throw new InvalidOperationException("Settings are not initialized.");
    internal AppState Model => _appModel;
    internal NodeCoordinator? NodeCoordinator => _nodeCoordinator;

    /// <summary>
    /// Ensures the managed SSH tunnel is started using the current settings.
    /// Used by the onboarding ConnectionPage when the user picks the SSH topology.
    /// </summary>
    public void EnsureSshTunnelStarted() => _sshTunnelService?.EnsureStarted(_settings);

    /// <summary>
    /// Creates the WSL local gateway setup engine using the current tray settings.
    /// Onboarding pages (Phase 5) call this to drive the local-WSL setup flow;
    /// the engine pairs the operator + Windows tray node into the gateway it
    /// installs, so we eagerly materialize the NodeService when needed.
    /// </summary>
    public LocalGatewaySetupEngine CreateLocalGatewaySetupEngine(
        bool replaceExistingConfigurationConfirmed = false)
    {
        var settings = _settings ?? new SettingsManager();
        var nodeService = EnsureNodeServiceForLocalGatewaySetup(settings);
        // Suppress node auto-connect in the connection manager during setup.
        // The engine controls node pairing in its own phase (PairWindowsTrayNode).
        _suppressNodeDuringSetup = true;
        try
        {
            // Use the connection manager's operator connector so all handshake/pairing
            // events appear in the diagnostics window and reuse the manager's v2/v3
            // signature fallback, credential resolution, and device token persistence.
            IGatewayOperatorConnector? operatorConnector = null;
            if (_connectionManager != null && _gatewayRegistry != null)
            {
                operatorConnector = new ConnectionManagerOperatorConnector(
                    _connectionManager, _gatewayRegistry, new AppLogger());
            }
            var engine = LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                new AppLogger(),
                nodeService,
                replaceExistingConfigurationConfirmed: replaceExistingConfigurationConfirmed,
                gatewayRegistry: _gatewayRegistry,
                operatorConnectorOverride: operatorConnector);
            // Clear suppress flag when engine completes so normal node connections resume.
            // Only clear if this engine is still the active one (prevents stale engine #1
            // from clearing the flag while engine #2 is running).
            var capturedEngine = engine;
            engine.StateChanged += (st) =>
            {
                if (st.Status is LocalGatewaySetupStatus.Complete or LocalGatewaySetupStatus.FailedTerminal
                    or LocalGatewaySetupStatus.FailedRetryable or LocalGatewaySetupStatus.Cancelled)
                {
                    if (_localSetupEngine == capturedEngine)
                        _suppressNodeDuringSetup = false;
                }
            };
            // Bug #2: cache so OnPairingStatusChanged can read engine.IsAutoPairingWindowsNode
            // and suppress the "copy pairing command" toast during the Phase 14 blip.
            _localSetupEngine = engine;
            return engine;
        }
        catch
        {
            _suppressNodeDuringSetup = false;
            throw;
        }
    }

    /// <summary>
    /// Returns the HWND of the active onboarding window, or IntPtr.Zero if none.
    /// Used by onboarding pages that need to host file pickers / dialogs.
    /// </summary>
    public IntPtr GetOnboardingWindowHandle()
        => _windowManager?.OnboardingWindow != null
            ? WinRT.Interop.WindowNative.GetWindowHandle(_windowManager.OnboardingWindow)
            : IntPtr.Zero;

    private SettingsManager? _settings;
    private SettingsData? _previousSettingsSnapshot;
    private SshTunnelService? _sshTunnelService;
    private GlobalHotkeyService? _globalHotkey;
    private DiagnosticsClipboardService? _diagnosticsCopy;
    private CommandCenterBuilder? _commandCenterBuilder;
    private ToastService? _toastService;
    private AppState _appModel = new();
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _deepLinkCts;
    private bool _isExiting;
    
    private GatewayService? _gatewayService;

    // FrozenDictionary for O(1) case-insensitive notification type → setting lookup — no per-call allocation.
    private static readonly System.Collections.Frozen.FrozenDictionary<string, Func<SettingsManager, bool>> s_notifTypeMap =
        new Dictionary<string, Func<SettingsManager, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["health"]    = s => s.NotifyHealth,
            ["urgent"]    = s => s.NotifyUrgent,
            ["reminder"]  = s => s.NotifyReminder,
            ["email"]     = s => s.NotifyEmail,
            ["calendar"]  = s => s.NotifyCalendar,
            ["build"]     = s => s.NotifyBuild,
            ["stock"]     = s => s.NotifyStock,
            ["info"]      = s => s.NotifyInfo,
            ["error"]     = s => s.NotifyUrgent,  // errors follow urgent setting
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Windows (created on demand — most moved to WindowManager)
    private WindowManager? _windowManager;
    private TrayActionDispatcher? _actionDispatcher;
    private TrayMenuWindow? _trayMenuWindow;

    // Bug 3: per-device idempotency for "Node paired" toast. WindowsNodeClient.HandleHelloOk
    // re-fires PairingStatusChanged(Paired) on every WS reconnect; we only want one toast
    // per device per session. (Source-side suppression also exists in WindowsNodeClient as
    // defense-in-depth.)
    // Node service (optional, enabled in settings)
    private NodeService? _nodeService;
    private NodeCoordinator? _nodeCoordinator;
    
    // Keep-alive window to anchor WinUI runtime (prevents GC/threading issues)
    private Window? _keepAliveWindow;

    private string[]? _startupArgs;
    private string? _pendingProtocolUri;
    // OPENCLAW_TRAY_DATA_DIR isolates a test instance: settings, logs, run marker,
    // crash log, exec approvals, and the single-instance mutex name all derive from it.
    private static readonly string? DataDirOverride =
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } v ? v : null;
    private static readonly string DataPath = DataDirOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
    // Operator/node identity store (DeviceIdentity). Lives at %APPDATA%\OpenClawTray
    // by convention so it follows the user across machines via roaming profile.
    // OPENCLAW_TRAY_APPDATA_DIR isolates a test/E2E identity store the same way
    // OPENCLAW_TRAY_DATA_DIR isolates the per-machine data directory.
    private static readonly string IdentityDataPath = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawTray");
    private static readonly string CrashLogPath = Path.Combine(DataPath, "crash.log");
    private static readonly string RunMarkerPath = Path.Combine(DataPath, "run.marker");

    public App()
    {
        // Language override for localization testing (e.g., OPENCLAW_LANGUAGE=zh-CN)
        var langOverride = Environment.GetEnvironmentVariable("OPENCLAW_LANGUAGE");
        if (!string.IsNullOrEmpty(langOverride))
        {
            // SECURITY: Whitelist known locale codes to prevent locale injection
            string[] allowedLocales = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw"];
            if (allowedLocales.Contains(langOverride.ToLowerInvariant()))
                LocalizationHelper.SetLanguageOverride(langOverride);
            else
                Logger.Warn($"[App] Ignoring invalid OPENCLAW_LANGUAGE value: {langOverride}");
        }

        InitializeComponent();
        
        CheckPreviousRun();
        MarkRunStarted();
        
        // Hook up crash handlers
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("UnhandledException", e.Exception);
        e.Handled = true; // Try to prevent crash
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogCrash("DomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTaskException", e.Exception);
        e.SetObserved(); // Prevent crash
    }
    
    private void OnProcessExit(object? sender, EventArgs e)
    {
        MarkRunEnded();
        try
        {
            Logger.Info($"Process exiting (ExitCode={Environment.ExitCode})");
        }
        catch { }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var message = $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n";
            File.AppendAllText(CrashLogPath, message);
        }
        catch { /* Can't log the crash logger crash */ }
        
        try
        {
            if (ex != null)
            {
                Logger.Error($"CRASH {source}: {ex}");
            }
            else
            {
                Logger.Error($"CRASH {source}");
            }
        }
        catch { /* Ignore logging failures */ }
    }
    
    private static void CheckPreviousRun()
    {
        try
        {
            if (File.Exists(RunMarkerPath))
            {
                var startedAt = File.ReadAllText(RunMarkerPath);
                Logger.Error($"Previous session did not exit cleanly (started {startedAt})");
                File.Delete(RunMarkerPath);
            }
        }
        catch { }
    }
    
    private static void MarkRunStarted()
    {
        try
        {
            var dir = Path.GetDirectoryName(RunMarkerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(RunMarkerPath, DateTime.Now.ToString("O"));
        }
        catch { }
    }
    
    private static void MarkRunEnded()
    {
        try
        {
            if (File.Exists(RunMarkerPath))
                File.Delete(RunMarkerPath);
        }
        catch { }
    }

    /// <summary>
    /// Check if the app was launched via protocol activation (MSIX deep link).
    /// In WinUI 3, protocol activation is retrieved via AppInstance, not OnActivated.
    /// </summary>
    private static string? GetProtocolActivationUri()
    {
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                && activatedArgs.Data is global::Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                return protocolArgs.Uri?.ToString();
            }
        }
        catch { /* Not activated via protocol, or not packaged */ }
        return null;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _startupArgs = Environment.GetCommandLineArgs();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Check for protocol activation (MSIX packaged apps receive deep links this way)
        string? protocolUri = GetProtocolActivationUri();

        // Single instance check - keep mutex alive for app lifetime.
        // When running with an isolated data dir (tests), suffix the mutex name so
        // the test instance does not collide with the user's regular tray app.
        // String.GetHashCode() is randomized per process since .NET Core 2.1, so
        // two test runs against the same data dir would otherwise pick different
        // mutex names — and `Math.Abs(int.MinValue)` overflows. Use a stable
        // SHA-256 prefix instead.
        var mutexName = "OpenClawTray";
        if (DataDirOverride is not null)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(DataDirOverride));
            mutexName = $"OpenClawTray-{Convert.ToHexString(hash, 0, 4)}";
        }
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            // Forward deep link args to running instance (command-line or protocol activation)
            var deepLink = protocolUri
                ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                    ? _startupArgs[1] : null);
            if (deepLink != null)
            {
                SendDeepLinkToRunningInstance(deepLink);
            }
            Exit();
            return;
        }

        // Store protocol URI for processing after setup
        _pendingProtocolUri = protocolUri;

        // Initialize settings before update check so skip selections can be remembered.
        _settings = new SettingsManager();
        _previousSettingsSnapshot = _settings.ToSettingsData();
        DiagnosticsJsonlService.Configure(DataPath);
        DiagnosticsJsonlService.Write("app.start", new
        {
            nodeMode = _settings.EnableNodeMode,
            useSshTunnel = _settings.UseSshTunnel
        });

        // Register URI scheme on first run
        DeepLinkHandler.RegisterUriScheme();

        // Check for updates before launching. Skip in test instances — no UI dialogs,
        // no network calls, no startup delay.
        if (DataDirOverride is null &&
            Environment.GetEnvironmentVariable("OPENCLAW_SKIP_UPDATE_CHECK") != "1")
        {
            var shouldLaunch = await CheckForUpdatesAsync();
            if (!shouldLaunch)
            {
                Exit();
                return;
            }
        }

        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        _sshTunnelService = new SshTunnelService(new AppLogger());
        _sshTunnelService.TunnelExited += OnSshTunnelExited;

        _diagnosticsCopy = new DiagnosticsClipboardService(() => _commandCenterBuilder!.Build());
        _toastService = new ToastService(_settings);
        _nodeCoordinator = new NodeCoordinator(
            _toastService, _settings, _dispatcherQueue!,
            () => _windowManager?.HubWindow, () => _localSetupEngine,
            () => UpdateTrayIcon());

        // Create WindowManager before tray icon so onboarding and
        // surface-improvements-tip can use it immediately.
        _windowManager = new WindowManager(
            _appModel,
            _settings,
            _dispatcherQueue!,
            () => _connectionManager,
            () => _gatewayRegistry,
            () => _nodeService,
            _nodeCoordinator,
            UpdateTrayIcon,
            IdentityDataPath,
            ResolveChatCredentialsTuple,
            CheckForUpdatesUserInitiatedAsync,
            _toastService);
        _windowManager.SettingsSaved += OnSettingsSaved;

        _actionDispatcher = new TrayActionDispatcher(
            _appModel,
            _windowManager,
            _settings,
            () => _connectionManager,
            () => _nodeService,
            _toastService,
            _diagnosticsCopy,
            _sshTunnelService,
            _gatewayRegistry,
            () => _keepAliveWindow,
            () => RunHealthCheckAsync(userInitiated: true),
            CheckForUpdatesUserInitiatedAsync,
            ExitApplication,
            EnsureSshTunnelConfigured,
            UpdateTrayIcon);

        // Wire the single dispatch callback now that both WindowManager and
        // TrayActionDispatcher exist.
        _windowManager.DispatchAction = action => _actionDispatcher.Dispatch(action);

        // Initialize tray icon FIRST(window-less pattern from WinUIEx).
        // The tray is application chrome and must always survive any failure
        // in the onboarding wizard. OnLaunched is async void, so a synchronous
        // throw inside the OnboardingWindow constructor would otherwise
        // propagate through `await ShowOnboardingAsync()` and abort OnLaunched
        // before the tray ever initializes.
        InitializeTrayIcon();
        _windowManager.ShowSurfaceImprovementsTipIfNeeded();

        // First-run check (also supports forced onboarding for testing).
        // Wrapped in try/catch so a wizard construction failure cannot tear
        // down the tray; user can retry via the Setup Guide menu item.
        try
        {
            if (RequiresSetup(_settings) ||
                Environment.GetEnvironmentVariable("OPENCLAW_FORCE_ONBOARDING") == "1")
            {
                await _windowManager.ShowOnboardingAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Onboarding failed during launch (tray remains available): {ex}");
        }

        // Initialize connection manager (north star architecture)
        _gatewayRegistry = new GatewayRegistry(SettingsManager.SettingsDirectoryPath);
        _gatewayRegistry.Load();
        var credentialResolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var clientFactory = new GatewayClientFactory();
        var appLogger = new AppLogger();
        var diagnostics = new ConnectionDiagnostics();
        var nodeConnector = new NodeConnector(appLogger, diagnostics);
        // Wrap the SSH tunnel service so the connection manager can start/stop the tunnel
        var tunnelManager = _sshTunnelService != null
            ? new SshTunnelManager(_sshTunnelService, appLogger)
            : null;
        _connectionManager = new GatewayConnectionManager(
            credentialResolver, clientFactory, _gatewayRegistry, appLogger,
            identityStore: new DeviceIdentityFileStore(appLogger),
            nodeConnector: nodeConnector,
            isNodeEnabled: ShouldInitializeNodeService,
            diagnostics: diagnostics,
            tunnelManager: tunnelManager);
        _connectionManager.OperatorClientChanged += OnOperatorClientChanged;
        _connectionManager.StateChanged += OnManagerStateChanged;

        _gatewayService = new GatewayService(_appModel, _dispatcherQueue!, () => _connectionManager?.OperatorClient);
        _gatewayService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _gatewayService.AuthenticationFailed += OnAuthenticationFailed;
        _gatewayService.SessionCommandCompleted += OnSessionCommandCompleted;
        _gatewayService.NotificationReceived += OnNotificationReceived;

        _commandCenterBuilder = new CommandCenterBuilder(
            _appModel,
            _settings,
            new NodeRuntimeInfoAdapter(() => _nodeService),
            _sshTunnelService,
            () => _gatewayService.LastCheckTime,
            () => _connectionManager?.OperatorClient);

        // Initialize connections — always create operator client for UI data,
        // additionally create node service for gateway node mode or local MCP.
        InitializeGatewayClient();

        // Pre-warm chat window (WebView2 init takes 1-3s, do it now so left-click is instant)
        if (_settings != null &&
            TryResolveChatCredentials(out var prewarmUrl, out var prewarmToken, out _, out var prewarmIsBootstrapToken) &&
            !prewarmIsBootstrapToken)
        {
            _windowManager.PrewarmChatWindow(prewarmUrl, prewarmToken);
            // Window is created but hidden — WebView2 initializes in the background
        }

        // Start deep link server
        StartDeepLinkServer();

        // Register global hotkey if enabled
        if (_settings.GlobalHotkeyEnabled)
        {
            _globalHotkey = new GlobalHotkeyService();
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.Register();
        }

        // Process startup deep link (command-line or MSIX protocol activation)
        var startupDeepLink = _pendingProtocolUri
            ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                ? _startupArgs[1] : null);
        if (startupDeepLink != null)
        {
            HandleDeepLink(startupDeepLink);
        }

        Logger.Info("Application started (WinUI 3)");
    }

    private void InitializeKeepAliveWindow()
    {
        // Create a hidden window to keep the WinUI runtime properly initialized
        // This prevents GC/threading issues when creating windows after idle
        _keepAliveWindow = new Window();
        _keepAliveWindow.Content = new Microsoft.UI.Xaml.Controls.Grid();
        _keepAliveWindow.AppWindow.IsShownInSwitchers = false;
        
        // Move off-screen and set minimal size
        _keepAliveWindow.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
    }

    private void InitializeTrayIcon()
    {
        // Initialize keep-alive window first to anchor WinUI runtime
        InitializeKeepAliveWindow();
        
        // Pre-create tray menu window at startup to avoid creation crashes later
        InitializeTrayMenuWindow();
        
        var iconPath = IconHelper.GetStatusIconPath(ConnectionStatus.Disconnected);
        _trayIcon = new TrayIcon(1, iconPath, BuildTrayTooltip());
        _trayIcon.IsVisible = true;
        ApplyTrayTooltip(BuildTrayTooltip());
        _trayIcon.Selected += OnTrayIconSelected;
        _trayIcon.ContextMenu += OnTrayContextMenu;
    }

    private void InitializeTrayMenuWindow()
    {
        // Pre-create menu window once - reuse to avoid crash on window creation after idle
        _trayMenuWindow = new TrayMenuWindow();
        _trayMenuWindow.MenuItemClicked += (s, action) => _actionDispatcher?.Dispatch(action);
        // Don't close - just hide
    }

    private void OnTrayIconSelected(TrayIcon sender, TrayIconEventArgs e)
    {
        _windowManager?.ShowChatWindow();
    }



    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs e)
    {
        // Right-click: show menu
        ShowTrayMenuPopup();
    }

    private async void ShowTrayMenuPopup()
    {
        try
        {
            // Verify dispatcher is still valid
            if (_dispatcherQueue == null)
            {
                Logger.Error("DispatcherQueue is null - cannot show menu");
                return;
            }

            // Menu uses purely cached data — no gateway requests on open
            // Data stays fresh via WebSocket event stream (session/health broadcasts)

            // Reuse pre-created window - never create new ones after startup
            if (_trayMenuWindow == null)
            {
                // This shouldn't happen, but recreate if needed
                Logger.Warn("TrayMenuWindow was null, recreating");
                InitializeTrayMenuWindow();
            }

            // Rebuild menu content
            _trayMenuWindow!.ClearItems();
            var menuState = new TrayMenuState
            {
                Status = _appModel.Status,
                GatewaySelf = _appModel.GatewaySelf,
                Settings = _settings,
                AuthFailureMessage = _appModel.AuthFailureMessage,
                Sessions = _appModel.Sessions,
                NodePairList = _appModel.NodePairList,
                DevicePairList = _appModel.DevicePairList,
                Nodes = _appModel.Nodes,
                Presence = _appModel.Presence,
                IdentityDataPath = IdentityDataPath,
                NodeServiceAvailable = _nodeService != null,
                NodeIsPaired = _nodeService?.IsPaired ?? false,
                NodeIsPendingApproval = _nodeService?.IsPendingApproval ?? false,
                NodeIsConnected = _nodeService?.IsConnected ?? false,
            };
            var menuCallbacks = new TrayMenuCallbacks
            {
                OnConnect = () => { _ = _connectionManager?.ReconnectAsync(); },
                OnDisconnect = () =>
                {
                    _ = _connectionManager?.DisconnectAsync();
                    _appModel.Sessions = Array.Empty<SessionInfo>();
                    _appModel.NodePairList = null;
                    _appModel.DevicePairList = null;
                    _appModel.ModelsList = null;
                    _appModel.ClearAgentEvents();
                    UpdateTrayIcon();
                    _appModel.Status = ConnectionStatus.Disconnected;
                },
                NavigateHub = (page) => _windowManager?.ShowHub(page),
                OnSettingsSaveAndReconnect = () => { _settings?.Save(); _ = _connectionManager?.ReconnectAsync(); },
            };
            TrayMenuBuilder.Build(_trayMenuWindow, menuState, menuCallbacks);
            _trayMenuWindow.ShowAtCursor();
        }
        catch (Exception ex)
        {
            LogCrash("ShowTrayMenuPopup", ex);
            Logger.Error($"Failed to show tray menu: {ex.Message}");
        }
    }

    private static string TruncateMenuText(string text, int maxLength = 96) =>
        MenuDisplayHelper.TruncateText(text, maxLength);

    private void AddRecentActivity(
        string line,
        string category = "general",
        string? icon = null,
        string? dashboardPath = null,
        string? details = null,
        string? sessionKey = null,
        string? nodeId = null)
    {
        ActivityStreamService.Add(
            category: category,
            title: line,
            icon: icon,
            details: details,
            dashboardPath: dashboardPath,
            sessionKey: sessionKey,
            nodeId: nodeId);
    }

    private List<string> GetRecentActivity(int maxItems)
    {
        return ActivityStreamService.GetItems(Math.Max(0, maxItems))
            .Select(item => $"{item.Timestamp:HH:mm:ss} {item.Title}")
            .ToList();
    }


    #region Gateway Client

    private void InitializeGatewayClient(bool useBootstrapHandoffAuth = false)
    {
        if (_settings == null || _connectionManager == null || _gatewayRegistry == null) return;
        // SSH tunnel lifecycle is now handled by the connection manager.

        var gatewayUrl = _settings.GetEffectiveGatewayUrl();

        // Check registry first — it's the source of truth after initial setup
        var activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            // Registry has an active gateway — connect directly
            _ = _connectionManager.ConnectAsync(activeRecord.Id);
            return;
        }

        TryMigrateLegacyGatewaySettings(gatewayUrl, new AppLogger());
        activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            _ = _connectionManager.ConnectAsync(activeRecord.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            if (TryStartLocalMcpOnlyNode())
                return;

            Logger.Info("Gateway URL not configured — skipping client initialization");
            return;
        }

        // Bridge: create/update a GatewayRecord from current settings URL.
        // Credentials come from GatewayRegistry and DeviceIdentity, not settings.
        var existing = _gatewayRegistry.FindByUrl(gatewayUrl);
        if (existing != null)
        {
            // Record already exists — just ensure it's active and connect
            _gatewayRegistry.SetActive(existing.Id);
        }
        else
        {
            // No record yet — create one from settings URL if we have a stored device token.
            var hasStoredDeviceToken = DeviceIdentity.HasStoredDeviceToken(
                Path.Combine(SettingsManager.SettingsDirectoryPath));
            if (!hasStoredDeviceToken)
            {
                if (TryStartLocalMcpOnlyNode())
                    return;

                Logger.Info("No stored device token — skipping startup connect (use Setup Code)");
                return;
            }

            var recordId = Guid.NewGuid().ToString();
            var record = new GatewayRecord
            {
                Id = recordId,
                Url = gatewayUrl,
                SshTunnel = _settings.UseSshTunnel
                    ? new SshTunnelConfig(
                        _settings.SshTunnelUser ?? "",
                        _settings.SshTunnelHost ?? "",
                        _settings.SshTunnelRemotePort,
                        _settings.SshTunnelLocalPort,
                        _settings.NodeBrowserProxyEnabled &&
                            SshTunnelCommandLine.CanForwardBrowserProxyPort(
                                _settings.SshTunnelRemotePort, _settings.SshTunnelLocalPort))
                    : null,
            };
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
        }

        var migratedRecord = _gatewayRegistry.GetActive()!;

        // Ensure identity directory exists for credential resolution
        var identityDir = _gatewayRegistry.GetIdentityDirectory(migratedRecord.Id);
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);

        // Copy identity file from legacy location if needed
        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        var newIdentityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (File.Exists(legacyIdentityPath) && !File.Exists(newIdentityPath))
        {
            try { File.Copy(legacyIdentityPath, newIdentityPath, overwrite: false); }
            catch (Exception ex) { Logger.Warn($"Failed to copy identity file: {ex.Message}"); }
        }

        // Delegate to connection manager — it creates the client, fires OperatorClientChanged,
        // and our handler re-wires the 27 event subscriptions
        _ = _connectionManager.ConnectAsync(migratedRecord.Id);
    }

    private void TryMigrateLegacyGatewaySettings(string gatewayUrl, IOpenClawLogger logger)
    {
        if (_settings == null || _gatewayRegistry == null || string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return;
        }

        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        if (!_settings.HasLegacyGatewayCredentials && !File.Exists(legacyIdentityPath))
        {
            return;
        }

        var migrated = _gatewayRegistry.MigrateFromSettings(
            gatewayUrl,
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken,
            _settings.UseSshTunnel,
            _settings.SshTunnelUser,
            _settings.SshTunnelHost,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort,
            SettingsManager.SettingsDirectoryPath,
            logger);

        if (migrated)
        {
            Logger.Info("[GatewayRegistry] Migrated legacy gateway settings into registry");
        }
    }

    private bool TryStartLocalMcpOnlyNode()
    {
        if (_settings == null || !_settings.EnableMcpServer || _settings.EnableNodeMode)
        {
            return false;
        }

        var nodeService = EnsureNodeServiceForLocalGatewaySetup(_settings);
        if (nodeService == null)
        {
            Logger.Warn("MCP-only mode requested but node service could not be initialized");
            return false;
        }

        try
        {
            nodeService.StartLocalOnlyAsync().GetAwaiter().GetResult();
            Logger.Info("Started MCP-only node service without gateway connection");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start MCP-only node service: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Handles the connection manager's OperatorClientChanged event.
    /// Subscribes to all gateway client events directly, replacing the former GatewayDataBridge.
    /// </summary>
    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        _gatewayService?.Attach(e.NewClient, e.OldClient, _settings);

        // Update UI references
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var hubWindow = _windowManager?.HubWindow;
            if (hubWindow != null && !hubWindow.IsClosed)
            {
                hubWindow.GatewayClient = _connectionManager?.OperatorClient;
                hubWindow.CurrentStatus = _appModel.Status;
            }
        });
    }

    /// <summary>
    /// Handles the connection manager's StateChanged event.
    /// Maps the snapshot to the existing tray icon / UI status system.
    /// </summary>
    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snap)
    {
        // Map OverallConnectionState to the existing ConnectionStatus enum
        // for backward compat with tray icon and hub window
        var mapped = snap.OverallState switch
        {
            OverallConnectionState.Idle => ConnectionStatus.Disconnected,
            OverallConnectionState.Connecting => ConnectionStatus.Connecting,
            OverallConnectionState.Connected => ConnectionStatus.Connected,
            OverallConnectionState.Ready => ConnectionStatus.Connected,
            OverallConnectionState.Degraded => ConnectionStatus.Connected,
            OverallConnectionState.PairingRequired => ConnectionStatus.Connecting,
            OverallConnectionState.Error => ConnectionStatus.Error,
            OverallConnectionState.Disconnecting => ConnectionStatus.Disconnected,
            _ => ConnectionStatus.Disconnected
        };

        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            _appModel.Status = mapped;
            UpdateTrayIcon();
        }
        else
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                _appModel.Status = mapped;
                UpdateTrayIcon();
            });
        }
    }

    private NodeService? EnsureNodeServiceForLocalGatewaySetup(SettingsManager settings)
    {
        if (_nodeService != null)
            return _nodeService;

        if (_dispatcherQueue == null)
            return null;

        try
        {
            _nodeService = new NodeService(
                new AppLogger(),
                _dispatcherQueue,
                DataPath,
                () => _keepAliveWindow?.Content as FrameworkElement,
                settings,
                enableMcpServer: settings.EnableMcpServer,
                identityDataPath: IdentityDataPath);
            _nodeService.StatusChanged += _nodeCoordinator!.OnNodeStatusChanged;
            _nodeService.NotificationRequested += _nodeCoordinator.OnNodeNotificationRequested;
            _nodeService.PairingStatusChanged += _nodeCoordinator.OnPairingStatusChanged;
            _nodeService.ChannelHealthUpdated += _gatewayService!.OnChannelHealthUpdated;
            _nodeService.InvokeCompleted += _nodeCoordinator.OnNodeInvokeCompleted;
            _nodeService.GatewaySelfUpdated += _gatewayService.OnGatewaySelfUpdated;
            return _nodeService;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service for local gateway setup: {ex}");
            _nodeService = null;
            return null;
        }
    }

    private void WireAppCapabilityHandlers()
    {
        var app = _nodeService?.AppCapability;
        if (app == null) return;

        app.NavigateHandler = async (page) =>
        {
            var tcs = new TaskCompletionSource<object?>();
            var queued = _dispatcherQueue?.TryEnqueue(() =>
            {
                try { _windowManager?.ShowHub(page); tcs.SetResult(new { navigated = true, page }); }
                catch (Exception ex) { tcs.SetResult(new { navigated = false, error = ex.Message }); }
            }) ?? false;
            if (!queued) tcs.TrySetResult(new { navigated = false, error = "UI thread unavailable" });
            return await tcs.Task;
        };

        app.StatusHandler = () => new
        {
            connectionStatus = _appModel.Status.ToString(),
            nodeConnected = _nodeService?.IsConnected ?? false,
            nodePaired = _nodeService?.IsPaired ?? false,
            nodePendingApproval = _nodeService?.IsPendingApproval ?? false,
            gatewayVersion = _appModel.GatewaySelf?.ServerVersion,
            sessionCount = _appModel.Sessions?.Length ?? 0,
            nodeCount = _appModel.Nodes?.Length ?? 0,
        };

        app.SessionsHandler = async (agentId) =>
        {
            var sessions = _appModel.Sessions ?? Array.Empty<SessionInfo>();
            if (!string.IsNullOrEmpty(agentId))
                sessions = sessions.Where(s => s.Key != null &&
                    s.Key.StartsWith($"agent:{agentId}:", StringComparison.OrdinalIgnoreCase)).ToArray();
            return sessions.Select(s => new { s.Key, s.Status, s.Model, s.AgeText, tokens = s.InputTokens + s.OutputTokens }).ToArray();
        };

        app.AgentsHandler = async () =>
        {
            if (_appModel.AgentsList.HasValue &&
                _appModel.AgentsList.Value.TryGetProperty("agents", out var agentsArr) &&
                agentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return System.Text.Json.JsonSerializer.Deserialize<object>(agentsArr.GetRawText());
            }
            return Array.Empty<object>();
        };

        app.NodesHandler = () =>
        {
            return _appModel.Nodes?.Select(n => new { n.DisplayName, n.NodeId, n.IsOnline, n.Platform, n.CapabilityCount }).ToArray()
                ?? Array.Empty<object>();
        };

        app.ConfigGetHandler = async (path) =>
        {
            if (_windowManager?.HubWindow?.LastConfig == null) return new { error = "Config not loaded" };
            // Config is already redacted by the gateway's redactConfigSnapshot
            var raw = _windowManager.HubWindow.LastConfig.Value;
            var config = raw.TryGetProperty("parsed", out var parsed) ? parsed
                : (raw.TryGetProperty("config", out var cfg) ? cfg : raw);
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var segment in path.Split('.'))
                {
                    if (config.TryGetProperty(segment, out var child)) config = child;
                    else return (object)new { error = $"Path not found: {path}" };
                }
            }
            return System.Text.Json.JsonSerializer.Deserialize<object>(config.GetRawText());
        };

        // Allowlist of safe settings (no secrets like Token, BootstrapToken, API keys)
        var safeSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AutoStart", "GlobalHotkeyEnabled", "ShowNotifications", "NotificationSound",
            "NotifyHealth", "NotifyUrgent", "NotifyReminder", "NotifyEmail", "NotifyCalendar",
            "NotifyBuild", "NotifyStock", "NotifyInfo", "NotifyChatResponses",
            "EnableNodeMode", "EnableMcpServer", "PreferStructuredCategories",
            "NodeCanvasEnabled", "NodeScreenEnabled", "NodeCameraEnabled",
            "NodeLocationEnabled", "NodeBrowserProxyEnabled", "NodeTtsEnabled",
            "HasSeenActivityStreamTip", "TtsProvider"
        };

        app.SettingsGetHandler = (name) =>
        {
            if (_settings == null) return null;
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(_settings);
        };

        app.SettingsSetHandler = (name, value) =>
        {
            if (_settings == null) return new { error = "Settings not loaded" };
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) return new { error = $"Unknown setting: {name}" };
            try
            {
                var converted = Convert.ChangeType(value, prop.PropertyType);
                prop.SetValue(_settings, converted);
                _settings.Save();
                return new { name, value = prop.GetValue(_settings) };
            }
            catch (Exception ex) { return new { error = ex.Message }; }
        };

        app.MenuHandler = () =>
        {
            var items = new List<object>
            {
                new { type = "status", status = _appModel.Status.ToString() },
                new { type = "sessions", count = _appModel.Sessions?.Length ?? 0 },
                new { type = "nodes", count = _appModel.Nodes?.Length ?? 0 },
            };
            return items;
        };

        app.SearchHandler = (query) =>
        {
            if (_windowManager?.HubWindow == null) return Array.Empty<object>();
            var commands = _windowManager.HubWindow.BuildCommandList();
            var matches = commands
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10)
                .Select(c => new { c.Title, c.Subtitle, c.Icon })
                .ToArray();
            return matches;
        };
    }

    private static bool RequiresSetup(SettingsManager settings)
    {
        return StartupSetupState.RequiresSetup(settings, IdentityDataPath);
    }

    private bool ShouldInitializeNodeService()
    {
        if (_suppressNodeDuringSetup) return false;
        return _settings?.EnableNodeMode == true || _settings?.EnableMcpServer == true;
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (status == ConnectionStatus.Connected)
            {
                var hubWindow = _windowManager?.HubWindow;
                if (hubWindow != null && !hubWindow.IsClosed)
                    hubWindow.LastAuthError = null;
            }

            UpdateTrayIcon();
        });
        
        if (status == ConnectionStatus.Connected)
        {
            _ = RunHealthCheckAsync();
        }
    }

    private void OnAuthenticationFailed(object? sender, string message)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            UpdateTrayIcon();

            // Forward to hub/connection page
            var hubWindow = _windowManager?.HubWindow;
            if (hubWindow != null && !hubWindow.IsClosed)
            {
                hubWindow.LastAuthError = message;
                hubWindow.UpdateStatus(_appModel.Status);
            }
        });
    }

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        if (_dispatcherQueue == null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var title = result.Ok ? "✅ Session updated" : "❌ Session action failed";
                var key = string.IsNullOrWhiteSpace(result.Key) ? "session" : result.Key!;
                var message = result.Ok
                    ? result.Method switch
                    {
                        "sessions.patch" => $"Updated settings for {key}",
                        "sessions.reset" => $"Reset {key}",
                        "sessions.compact" => result.Kept.HasValue
                            ? $"Compacted {key} ({result.Kept.Value} lines kept)"
                            : $"Compacted {key}",
                        "sessions.delete" => $"Deleted {key}",
                        _ => $"Completed action for {key}"
                    }
                    : result.Error ?? "Request failed";
                AddRecentActivity(
                    $"{title.Replace("✅ ", "").Replace("❌ ", "")}: {message}",
                    category: "session",
                    dashboardPath: !string.IsNullOrWhiteSpace(result.Key) ? $"sessions/{result.Key}" : "sessions",
                    sessionKey: result.Key);

                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to show session action toast: {ex.Message}");
            }
        });

        if (result.Ok)
        {
            _ = _connectionManager?.OperatorClient?.RequestSessionsAsync();
        }
    }


    private void OnNotificationReceived(object? sender, OpenClawNotification notification)
    {
        // Voice overlay: show agent chat responses, and (independently) speak them
        // if the user enabled "Read responses aloud". TTS used to be gated on
        // an active voice overlay session — we want the toggle to honor every
        // chat reply now that voice and text chat will eventually share one UI.
        if (notification.IsChat && !string.IsNullOrEmpty(notification.Message))
        {
            if (_windowManager?.VoiceOverlayWindow != null)
            {
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    try
                    {
                        _windowManager?.VoiceOverlayWindow?.AddAgentResponse(notification.Message);
                    }
                    catch { }
                });
            }

            // TTS: read response aloud whenever the toggle is on (any chat surface).
            if (_settings?.VoiceTtsEnabled == true)
            {
                _ = SpeakResponseAsync(notification.Message);
            }
        }

        if (_settings?.ShowNotifications != true) return;
        if (!ShouldShowNotification(notification)) return;

        // Store in history
        NotificationHistoryService.AddNotification(new Services.GatewayNotification
        {
            Title = notification.Title,
            Message = notification.Message,
            Category = notification.Type
        });

        // Show toast
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(notification.Title ?? "OpenClaw")
                .AddText(notification.Message);

            // Add category-specific inline image (emoji rendered as text is fine, 
            // but we can add app logo override for better visibility)
            var logoPath = ToastService.GetNotificationIcon(notification.Type);
            if (!string.IsNullOrEmpty(logoPath) && System.IO.File.Exists(logoPath))
            {
                builder.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Circle);
            }

            // Add "Open Chat" button for chat notifications
            if (notification.IsChat)
            {
                builder.AddArgument("action", "open_chat")
                       .AddButton(new ToastButton()
                           .SetContent("Open Chat")
                           .AddArgument("action", "open_chat"));
            }

            _toastService?.ShowToast(builder);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show toast: {ex.Message}");
        }
    }

    private bool ShouldShowNotification(OpenClawNotification notification)
    {
        if (_settings == null) return true;

        // Chat toggle: suppress all chat responses if disabled
        if (notification.IsChat && !_settings.NotifyChatResponses)
            return false;

        // Suppress chat notifications when a chat window is already showing them
        if (notification.IsChat)
        {
            var hubWindow = _windowManager?.HubWindow;
            if (hubWindow != null && !hubWindow.IsClosed)
                return false;
            if (_windowManager?.OnboardingWindow != null)
                return false; // Onboarding window has chat overlay
        }

        var type = notification.Type;
        if (type == null) return true;
        return s_notifTypeMap.TryGetValue(type, out var selector) ? selector(_settings) : true;
    }

    /// <summary>User-initiated health check (from UI button). No background timers.</summary>
    private async Task RunHealthCheckAsync(bool userInitiated = false)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null)
        {
            if (_settings?.EnableNodeMode == true && _nodeService?.IsConnected == true)
            {
                if (_gatewayService != null) _gatewayService.LastCheckTime = DateTime.Now;
                if (userInitiated)
                {
                    _toastService?.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                        .AddText("Node Mode is connected; gateway health is streaming."));
                }
                return;
            }

            if (userInitiated)
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckNotConnected")));
            }
            return;
        }

        try
        {
            if (_gatewayService != null) _gatewayService.LastCheckTime = DateTime.Now;
            await client.CheckHealthAsync();
            if (userInitiated)
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckSent")));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Health check failed: {ex.Message}");
            if (userInitiated)
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckFailed"))
                    .AddText(ex.Message));
            }
        }
    }

    #endregion

    #region Tray Icon

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null) return;

        var status = _appModel.Status;
        if (_appModel.CurrentActivity != null && _appModel.CurrentActivity.Kind != OpenClaw.Shared.ActivityKind.Idle)
        {
            status = ConnectionStatus.Connecting; // Use connecting icon for activity
        }

        var iconPath = IconHelper.GetStatusIconPath(status);
        var tooltip = BuildTrayTooltip();

        try
        {
            _trayIcon.SetIcon(iconPath);
            ApplyTrayTooltip(tooltip);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update tray icon: {ex.Message}");
        }
    }

    private void ApplyTrayTooltip(string tooltip)
    {
        if (_trayIcon == null)
            return;

        if (string.Equals(_trayIcon.Tooltip, tooltip, StringComparison.Ordinal))
        {
            _trayIcon.Tooltip = string.Empty;
        }

        _trayIcon.Tooltip = tooltip;
    }

    private string BuildTrayTooltip()
    {
        var topology = GatewayTopologyClassifier.Classify(
            _settings?.GatewayUrl,
            _settings?.UseSshTunnel == true,
            _settings?.SshTunnelHost,
            _settings?.SshTunnelLocalPort ?? 0,
            _settings?.SshTunnelRemotePort ?? 0);
        var channelReady = _appModel.Channels.Count(c => ChannelHealth.IsHealthyStatus(c.Status));
        var nodeOnline = _appModel.Nodes.Count(n => n.IsOnline);
        var nodeTotal = _appModel.Nodes.Length;
        if (nodeTotal == 0 && _nodeService?.GetLocalNodeInfo() is { } localNode)
        {
            nodeTotal = 1;
            nodeOnline = localNode.IsOnline ? 1 : 0;
        }

        var warningCount = 0;
        if (_appModel.Status != ConnectionStatus.Connected)
            warningCount++;
        if (_appModel.AuthFailureMessage != null)
            warningCount++;
        if (_appModel.Channels.Length == 0 && _appModel.Status == ConnectionStatus.Connected)
            warningCount++;

        var tooltip = new List<string>
        {
            $"OpenClaw Tray — {_appModel.Status}",
            $"Topology: {topology.DisplayName}",
            $"Channels: {channelReady}/{_appModel.Channels.Length} ready · Nodes: {nodeOnline}/{nodeTotal} online",
            $"Warnings: {warningCount} · Last check: {(_gatewayService?.LastCheckTime ?? DateTime.Now):HH:mm:ss}"
        };

        if (_appModel.CurrentActivity != null && !string.IsNullOrEmpty(_appModel.CurrentActivity.DisplayText))
        {
            tooltip.Insert(1, _appModel.CurrentActivity.DisplayText);
        }

        return TrayTooltipFormatter.FitShellTooltip(string.Join("; ", tooltip));
    }

    #endregion

    #region Window Management

    /// <summary>
    /// Forwarding method for callers that reference App.ShowHub directly
    /// (e.g. OnboardingWindow, AppCapability NavigateHandler).
    /// </summary>
    internal void ShowHub(string? navigateTo = null, bool activate = true)
        => _windowManager?.ShowHub(navigateTo, activate);

    private void OnSettingsCommandCenterRequested(object? sender, EventArgs e)
    {
        _windowManager?.ShowStatusDetail();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        var currentSnapshot = _settings?.ToSettingsData();
        var impact = SettingsChangeClassifier.Classify(_previousSettingsSnapshot, currentSnapshot);
        _previousSettingsSnapshot = currentSnapshot;
        Logger.Info($"[SETTINGS] Change impact: {impact}");

        switch (impact)
        {
            case SettingsChangeImpact.FullReconnectRequired:
            case SettingsChangeImpact.OperatorReconnectRequired:
                // Full reconnect: tear down everything and rebuild
                _appModel.GatewaySelf = null;
                if (_settings?.UseSshTunnel != true)
                {
                    _sshTunnelService?.Stop();
                }
                // Status is updated by OnManagerStateChanged when reconnect starts.
                _appModel.Status = ConnectionStatus.Disconnected;
                UpdateTrayIcon();

                // Reset chat window — it has a stale URL/token
                _windowManager?.ResetChatWindow();

                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.NodeReconnectRequired:
                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.CapabilityReload:
                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.UiOnly:
            case SettingsChangeImpact.NoOp:
                // No connection changes needed
                break;
        }

        // Non-connection settings always applied regardless of impact
        if (_settings!.GlobalHotkeyEnabled)
        {
            _globalHotkey ??= new GlobalHotkeyService();
            _globalHotkey.HotkeyPressed -= OnGlobalHotkeyPressed;
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
            _globalHotkey.Register();
        }
        else
        {
            _globalHotkey?.Unregister();
        }

        AutoStartManager.SetAutoStart(_settings.AutoStart);

        // Keep hub window in sync
        var hubWindow = _windowManager?.HubWindow;
        if (hubWindow != null && !hubWindow.IsClosed)
        {
            hubWindow.Settings = _settings;
            hubWindow.GatewayClient = _connectionManager?.OperatorClient;
            hubWindow.CurrentStatus = _appModel.Status;
        }
    }

    private async Task RefreshCommandCenterAsync()
    {
        await RunHealthCheckAsync(userInitiated: true);
        var client = _connectionManager?.OperatorClient;
        if (client != null)
        {
            await client.RequestSessionsAsync();
            await client.RequestUsageAsync();
            await client.RequestNodesAsync();
        }
        UpdateStatusDetailWindow();
    }

    private void UpdateStatusDetailWindow()
    {
        // Status changes are observed by HubWindow via AppState.PropertyChanged.
        // This method is kept as a placeholder for any future non-hub status detail updates.
    }

    #endregion

    private bool TryResolveChatCredentials(
        out string gatewayUrl,
        out string token,
        out string credentialSource,
        out bool isBootstrapToken)
    {
        gatewayUrl = string.Empty;
        token = string.Empty;
        credentialSource = "none";
        isBootstrapToken = false;

        if (_settings == null)
            return false;

        if (!InteractiveGatewayCredentialResolver.TryResolve(
            _settings,
            _gatewayRegistry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
            out var credential) ||
            credential == null)
        {
            return false;
        }

        gatewayUrl = credential.GatewayUrl;
        token = credential.Token;
        credentialSource = credential.Source;
        isBootstrapToken = credential.IsBootstrapToken;
        return true;
    }

    /// <summary>
    /// Tuple adapter for TryResolveChatCredentials, used by WindowManager.
    /// </summary>
    private (bool ok, string url, string token, string source, bool isBootstrap) ResolveChatCredentialsTuple()
    {
        if (TryResolveChatCredentials(out var url, out var token, out var source, out var isBootstrap))
            return (true, url, token, source, isBootstrap);
        return (false, string.Empty, string.Empty, "none", false);
    }

    #region Actions

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null)
        {
            Logger.Warn("Hotkey pressed but DispatcherQueue is null");
            return;
        }

        var enqueued = _dispatcherQueue.TryEnqueue(() => _windowManager?.ShowQuickSend());
        if (!enqueued)
        {
            Logger.Warn("Hotkey pressed but failed to enqueue QuickSend on UI thread");
        }
    }

    private void OnVoiceHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;
        _dispatcherQueue.TryEnqueue(() => _windowManager?.ShowVoiceOverlay());
    }

    #endregion

    #region Updates



    private async Task<bool> CheckForUpdatesAsync()
    {
        try
        {
#if DEBUG
            Logger.Info("Skipping update check in debug build");
            _appModel.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = "debug build"
            };
            return true;
#else
            Logger.Info("Checking for updates...");
            _appModel.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Checking",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow
            };
            var updateFound = await AppUpdater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                Logger.Info("No updates available");
                _appModel.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Current",
                    CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                    CheckedAt = DateTime.UtcNow,
                    Detail = "no updates available"
                };
                return true;
            }

            var release = AppUpdater.LatestRelease!;
            var changelog = AppUpdater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {release.TagName}");
            _appModel.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Available",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                LatestVersion = release.TagName,
                CheckedAt = DateTime.UtcNow,
                Detail = "prompted"
            };

            if (!string.IsNullOrWhiteSpace(_settings?.SkippedUpdateTag) &&
                string.Equals(_settings.SkippedUpdateTag, release.TagName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"Skipping update prompt for remembered version {release.TagName}");
                _appModel.UpdateInfo.Detail = "skipped by user";
                return true;
            }

            var dialog = new UpdateDialog(release.TagName, changelog);
            var result = await dialog.ShowAsync();

            if (result == UpdateDialogResult.Download)
            {
                _appModel.UpdateInfo.Detail = "download requested";
                if (_settings != null)
                {
                    _settings.SkippedUpdateTag = string.Empty;
                    _settings.Save();
                }
                var installed = await DownloadAndInstallUpdateAsync();
                return !installed; // Don't launch if update succeeded
            }

            if (result == UpdateDialogResult.Skip && _settings != null)
            {
                _settings.SkippedUpdateTag = release.TagName ?? string.Empty;
                _settings.Save();
                _appModel.UpdateInfo.Detail = "skipped by user";
            }

            return true; // RemindLater or Skip - continue
#endif
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            _appModel.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = ex.Message
            };
            return true;
        }
    }

    private async Task CheckForUpdatesUserInitiatedAsync()
    {
        Logger.Info("Manual update check requested");
        var shouldContinue = await CheckForUpdatesAsync();
        UpdateStatusDetailWindow();
        if (!shouldContinue)
        {
            Exit();
        }
    }

    private async Task<bool> DownloadAndInstallUpdateAsync()
    {
        DownloadProgressDialog? progressDialog = null;
        try
        {
            progressDialog = new DownloadProgressDialog(AppUpdater);
            progressDialog.ShowAsync(); // Fire and forget

            var downloadedAsset = await AppUpdater.DownloadUpdateAsync();

            progressDialog?.Close();

            if (downloadedAsset == null || !System.IO.File.Exists(downloadedAsset.FilePath))
            {
                Logger.Error("Update download failed or file missing");
                return false;
            }

            Logger.Info("Installing update and restarting...");
            await AppUpdater.InstallUpdateAsync(downloadedAsset);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed: {ex.Message}");
            progressDialog?.Close();
            return false;
        }
    }

    #endregion

    #region Deep Links

    private void StartDeepLinkServer()
    {
        _deepLinkCts = new CancellationTokenSource();
        var token = _deepLinkCts.Token;
        
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await pipe.WaitForConnectionAsync(token);
                    using var reader = new System.IO.StreamReader(pipe);
                    var uri = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrEmpty(uri))
                    {
                        Logger.Info($"Received deep link via IPC: {uri}");
                        _dispatcherQueue?.TryEnqueue(() => HandleDeepLink(uri));
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Deep link server stopping (canceled)");
                    break; // Normal shutdown
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Logger.Warn($"Deep link server error: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch { break; }
                    }
                }
            }
        }, token);
    }

    private void HandleDeepLink(string uri)
    {
        DeepLinkHandler.Handle(uri, new DeepLinkActions
        {
            OpenSettings = () => _windowManager?.ShowSettings(),
            OpenSetup = () => _ = _windowManager?.ShowOnboardingAsync(),
            RunHealthCheck = () => RunHealthCheckAsync(userInitiated: true),
            CheckForUpdates = CheckForUpdatesUserInitiatedAsync,
            OpenLogFile = () => _actionDispatcher?.OpenLogFile(),
            OpenLogFolder = () => _actionDispatcher?.OpenLogFolder(),
            OpenConfigFolder = () => _actionDispatcher?.OpenConfigFolder(),
            OpenDiagnosticsFolder = () => _actionDispatcher?.OpenDiagnosticsFolder(),
            OpenConnectionStatus = () => _windowManager?.ShowConnectionStatusWindow(),
            CopySupportContext = () => _diagnosticsCopy?.CopySupportContext(),
            CopyDebugBundle = () => _diagnosticsCopy?.CopyDebugBundle(),
            CopyBrowserSetupGuidance = () => _diagnosticsCopy?.CopyBrowserSetupGuidance(),
            CopyPortDiagnostics = () => _diagnosticsCopy?.CopyPortDiagnostics(),
            CopyCapabilityDiagnostics = () => _diagnosticsCopy?.CopyCapabilityDiagnostics(),
            CopyNodeInventory = () => _diagnosticsCopy?.CopyNodeInventory(),
            CopyChannelSummary = () => _diagnosticsCopy?.CopyChannelSummary(),
            CopyActivitySummary = () => _diagnosticsCopy?.CopyActivitySummary(),
            CopyExtensibilitySummary = () => _diagnosticsCopy?.CopyExtensibilitySummary(),
            RestartSshTunnel = () => _actionDispatcher?.Dispatch("restartsshtunnel"),
            OpenChat = () => _windowManager?.ShowWebChat(),
            OpenCommandCenter = () => _windowManager?.ShowStatusDetail(),
            OpenTrayMenu = ShowTrayMenuPopup,
            OpenActivityStream = (f) => _windowManager?.ShowActivityStream(f),
            OpenNotificationHistory = () => _windowManager?.ShowNotificationHistory(),
            OpenDashboard = (path) => _actionDispatcher?.OpenDashboard(path),
            OpenQuickSend = (msg) => _windowManager?.ShowQuickSend(msg),
            OpenHub = (page) => _windowManager?.ShowHub(page),
            OpenVoice = () => _windowManager?.ShowVoiceOverlay(),
            StopVoice = () => _ = StopVoiceAsync(),
            SendMessage = async (msg) =>
            {
                var client = _connectionManager?.OperatorClient;
                if (client != null)
                {
                    await client.SendChatMessageAsync(msg);
                }
            }
        });
    }

    private async Task StopVoiceAsync()
    {
        var voiceService = _nodeService?.VoiceService;
        if (voiceService != null)
            await voiceService.StopAsync();
    }

    private int _ttsMuteCount;

    private async Task SpeakResponseAsync(string text)
    {
        var voiceService = _nodeService?.VoiceService;
        var ttsService = _nodeService?.TextToSpeech;
        try
        {
            if (voiceService == null || _settings == null || ttsService == null) return;

            // Increment mute counter — multiple concurrent TTS won't unmute prematurely
            Interlocked.Increment(ref _ttsMuteCount);
            voiceService.IsMutedForPlayback = true;

            var speakText = text.Length > 500 ? text[..500] + "..." : text;

            // Don't pass VoiceId here. The shared TextToSpeechService picks
            // the right per-provider voice from settings (TtsPiperVoiceId,
            // TtsWindowsVoiceId, TtsElevenLabsVoiceId). Cross-provider
            // voice IDs would otherwise leak across providers.
            var speakArgs = new OpenClaw.Shared.Capabilities.TtsSpeakArgs
            {
                Text = speakText,
                Provider = _settings.TtsProvider ?? TtsCapability.PiperProvider,
                Interrupt = true
            };

            await ttsService.SpeakAsync(speakArgs);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TTS response playback failed: {ex.Message}");
        }
        finally
        {
            // Only unmute when all concurrent TTS operations have finished
            if (voiceService != null)
            {
                await Task.Delay(300);
                if (Interlocked.Decrement(ref _ttsMuteCount) <= 0)
                    voiceService.IsMutedForPlayback = false;
            }
        }
    }

    private static void SendDeepLinkToRunningInstance(string uri)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new System.IO.StreamWriter(pipe);
            writer.WriteLine(uri);
            writer.Flush();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to forward deep link: {ex.Message}");
        }
    }

    #endregion

    #region Toast Activation

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var arguments = ToastArguments.Parse(args.Argument);
        
        if (arguments.TryGetValue("action", out var action))
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                switch (action)
                {
                    case "open_url" when arguments.TryGetValue("url", out var url):
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { }
                        break;
                    case "open_dashboard":
                        _actionDispatcher?.OpenDashboard();
                        break;
                    case "open_settings":
                        _windowManager?.ShowSettings();
                        break;
                    case "open_chat":
                        _windowManager?.ShowWebChat();
                        break;
                    case "open_activity":
                        _windowManager?.ShowActivityStream();
                        break;
                    case "copy_pairing_command" when arguments.TryGetValue("command", out var command):
                        CopyTextToClipboard(command);
                        _toastService?.ShowToast(new ToastContentBuilder()
                            .AddText(LocalizationHelper.GetString("Toast_PairingCommandCopied"))
                            .AddText(command));
                        break;
                }
            });
        }
    }

    public static void CopyTextToClipboard(string text)
    {
        var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    #endregion

    #region Exit

    private void ExitApplication()
    {
        if (_isExiting)
        {
            Logger.Info("Exit requested while shutdown already in progress");
            return;
        }

        _isExiting = true;
        Logger.Info("Application exiting");

        // Cancel background tasks
        if (_deepLinkCts != null)
        {
            Logger.Info("Shutdown: canceling deep link server");
            try { _deepLinkCts.Cancel(); } catch (Exception ex) { Logger.Warn($"Shutdown: deep link cancel failed: {ex.Message}"); }
        }

        // Cleanup hotkey
        SafeShutdownStep("global hotkey", () =>
        {
            _globalHotkey?.Dispose();
            _globalHotkey = null;
        });

        // Dispose runtime services
        SafeShutdownStep("gateway client", () =>
        {
            _connectionManager?.Dispose();
        });

        SafeShutdownStep("node service", () =>
        {
            _nodeService?.Dispose();
            _nodeService = null;
        });

        SafeShutdownStep("standalone voice service", () =>
        {
            _windowManager?.DisposeStandaloneVoiceService();
        });

        SafeShutdownStep("ssh tunnel service", () =>
        {
            _sshTunnelService?.Dispose();
            _sshTunnelService = null;
        });

        // Close windows explicitly for deterministic shutdown tracing.
        SafeShutdownStep("chat window", () => _windowManager?.CloseChatWindow());
        SafeShutdownStep("tray menu window", () => CloseWindow(_trayMenuWindow));
        _trayMenuWindow = null;
        SafeShutdownStep("quick send dialog", () => _windowManager?.CloseQuickSendDialog());
        SafeShutdownStep("keep alive window", () => CloseWindow(_keepAliveWindow));
        _keepAliveWindow = null;

        // Dispose tray and mutex
        SafeShutdownStep("tray icon", () =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        });

        SafeShutdownStep("single-instance mutex", () =>
        {
            _mutex?.Dispose();
            _mutex = null;
        });

        // Dispose cancellation token source
        SafeShutdownStep("deep link token source", () =>
        {
            _deepLinkCts?.Dispose();
            _deepLinkCts = null;
        });

        Logger.Info("Shutdown complete; calling Exit() now");
        Exit();
    }

    private static void CloseWindow(Window? window)
    {
        try
        {
            window?.Close();
        }
        catch
        {
            // Let caller log specific failure context.
            throw;
        }
    }

    private static void SafeShutdownStep(string name, Action action)
    {
        try
        {
            Logger.Info($"Shutdown: disposing {name}");
            action();
            Logger.Info($"Shutdown: disposed {name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shutdown: failed disposing {name}: {ex.Message}");
        }
    }

    private bool EnsureSshTunnelConfigured()
    {
        if (_settings == null)
        {
            return false;
        }

        if (_settings.UseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(_settings.SshTunnelUser) ||
                string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ||
                _settings.SshTunnelRemotePort is < 1 or > 65535 ||
                _settings.SshTunnelLocalPort is < 1 or > 65535)
            {
                Logger.Warn("SSH tunnel is enabled but settings are incomplete");
                _appModel.Status = ConnectionStatus.Error;
                UpdateTrayIcon();
                return false;
            }

            try
            {
                _sshTunnelService ??= new SshTunnelService(new AppLogger());
                _sshTunnelService.EnsureStarted(_settings);
                DiagnosticsJsonlService.Write("tunnel.ensure_started", new
                {
                    status = _sshTunnelService.Status.ToString(),
                    localEndpoint = $"127.0.0.1:{_settings.SshTunnelLocalPort}",
                    remoteHost = string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ? null : _settings.SshTunnelHost,
                    remotePort = _settings.SshTunnelRemotePort
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start SSH tunnel: {ex.Message}");
                _appModel.Status = ConnectionStatus.Error;
                UpdateTrayIcon();
                return false;
            }
        }
        else
        {
            _sshTunnelService?.Stop();
        }

        return true;
    }

    #endregion

    private async void OnSshTunnelExited(object? sender, int exitCode)
    {
        Logger.Warn($"SSH tunnel exited unexpectedly (code {exitCode}); restarting in 3s...");
        _sshTunnelService?.MarkRestarting(exitCode);
        DiagnosticsJsonlService.Write("tunnel.restart_scheduled", new
        {
            exitCode,
            localEndpoint = _sshTunnelService?.CurrentLocalPort > 0
                ? $"127.0.0.1:{_sshTunnelService.CurrentLocalPort}"
                : null
        });
        await Task.Delay(3000);
        if (_sshTunnelService != null && _settings?.UseSshTunnel == true)
        {
            try
            {
                _sshTunnelService.EnsureStarted(_settings);
                Logger.Info("SSH tunnel restarted successfully");
                DiagnosticsJsonlService.Write("tunnel.restart_succeeded", new
                {
                    localEndpoint = _sshTunnelService.CurrentLocalPort > 0
                        ? $"127.0.0.1:{_sshTunnelService.CurrentLocalPort}"
                        : null
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"SSH tunnel restart failed: {ex.Message}");
                DiagnosticsJsonlService.Write("tunnel.restart_failed", new { ex.Message });
            }
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueue? AppDispatcherQueue =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
}

internal class AppLogger : IOpenClawLogger
{
    public void Info(string message) => Logger.Info(message);
    public void Debug(string message) => Logger.Debug(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message, Exception? ex = null) => 
        Logger.Error(ex != null ? $"{message}: {ex.Message}" : message);
}
