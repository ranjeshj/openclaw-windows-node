using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services.Connection;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Routes string-based actions from tray menu clicks and deep links to the
/// appropriate handler.  Extracted from App.xaml.cs to reduce its size and
/// isolate action dispatch logic.
/// </summary>
internal sealed class TrayActionDispatcher
{
    private readonly AppState _appModel;
    private readonly WindowManager _windowManager;
    private readonly SettingsManager? _settings;
    private readonly Func<GatewayConnectionManager?> _connectionManagerProvider;
    private readonly Func<NodeService?> _nodeServiceProvider;
    private readonly ToastService? _toastService;
    private readonly DiagnosticsClipboardService? _diagnosticsCopy;
    private readonly SshTunnelService? _sshTunnelService;
    private readonly GatewayRegistry? _gatewayRegistry;
    private readonly Func<Window?> _keepAliveWindowProvider;
    private readonly Func<Task> _runHealthCheck;
    private readonly Func<Task> _checkForUpdates;
    private readonly Action _exitApplication;
    private readonly Func<bool> _ensureSshTunnelConfigured;
    private readonly Action _updateTrayIcon;

    public TrayActionDispatcher(
        AppState appModel,
        WindowManager windowManager,
        SettingsManager? settings,
        Func<GatewayConnectionManager?> connectionManagerProvider,
        Func<NodeService?> nodeServiceProvider,
        ToastService? toastService,
        DiagnosticsClipboardService? diagnosticsCopy,
        SshTunnelService? sshTunnelService,
        GatewayRegistry? gatewayRegistry,
        Func<Window?> keepAliveWindowProvider,
        Func<Task> runHealthCheck,
        Func<Task> checkForUpdates,
        Action exitApplication,
        Func<bool> ensureSshTunnelConfigured,
        Action updateTrayIcon)
    {
        _appModel = appModel;
        _windowManager = windowManager;
        _settings = settings;
        _connectionManagerProvider = connectionManagerProvider;
        _nodeServiceProvider = nodeServiceProvider;
        _toastService = toastService;
        _diagnosticsCopy = diagnosticsCopy;
        _sshTunnelService = sshTunnelService;
        _gatewayRegistry = gatewayRegistry;
        _keepAliveWindowProvider = keepAliveWindowProvider;
        _runHealthCheck = runHealthCheck;
        _checkForUpdates = checkForUpdates;
        _exitApplication = exitApplication;
        _ensureSshTunnelConfigured = ensureSshTunnelConfigured;
        _updateTrayIcon = updateTrayIcon;
    }

    /// <summary>
    /// Dispatches a string action to the appropriate handler.
    /// Called by tray menu clicks and deep link handlers.
    /// </summary>
    public void Dispatch(string action)
    {
        switch (action)
        {
            case "status": _windowManager.ShowStatusDetail(); break;
            case "reconnect": _ = _connectionManagerProvider()?.ReconnectAsync(); break;
            case "disconnect":
                _ = _connectionManagerProvider()?.DisconnectAsync();
                _appModel.Status = ConnectionStatus.Disconnected;
                _updateTrayIcon();
                break;
            case "dashboard": OpenDashboard(); break;
            case "canvas": _windowManager.ShowCanvasWindow(); break;
            case "openchat": _windowManager.ShowChatWindow(); break;
            case "voice": _windowManager.ShowVoiceOverlay(); break;
            case "webchat": _windowManager.ShowWebChat(); break;
            case "hub": _windowManager.ShowHub(); break;
            case "companion":
                if (_appModel.Status != ConnectionStatus.Connected)
                    _windowManager.ShowHub("general");
                else
                    _windowManager.ShowHub();
                break;
            case "quicksend": _windowManager.ShowQuickSend(); break;
            case "history": _windowManager.ShowNotificationHistory(); break;
            case "activity": _windowManager.ShowActivityStream(); break;
            case "healthcheck": _ = _runHealthCheck(); break;
            case "checkupdates": _ = _checkForUpdates(); break;
            case "settings": _windowManager.ShowSettings(); break;
            case "setup": _ = _windowManager.ShowOnboardingAsync(); break;
            case "autostart": ToggleAutoStart(); break;
            case "log": OpenLogFile(); break;
            case "logfolder": OpenLogFolder(); break;
            case "configfolder": OpenConfigFolder(); break;
            case "diagnosticsfolder": OpenDiagnosticsFolder(); break;
            case "connectionstatus": _windowManager.ShowConnectionStatusWindow(); break;
            case "supportcontext": _diagnosticsCopy?.CopySupportContext(); break;
            case "debugbundle": _diagnosticsCopy?.CopyDebugBundle(); break;
            case "browsersetup": _diagnosticsCopy?.CopyBrowserSetupGuidance(); break;
            case "portdiagnostics": _diagnosticsCopy?.CopyPortDiagnostics(); break;
            case "capabilitydiagnostics": _diagnosticsCopy?.CopyCapabilityDiagnostics(); break;
            case "nodeinventory": _diagnosticsCopy?.CopyNodeInventory(); break;
            case "channelsummary": _diagnosticsCopy?.CopyChannelSummary(); break;
            case "activitysummary": _diagnosticsCopy?.CopyActivitySummary(); break;
            case "extensibilitysummary": _diagnosticsCopy?.CopyExtensibilitySummary(); break;
            case "restartsshtunnel": RestartSshTunnel(); break;
            case "copydeviceid": CopyDeviceIdToClipboard(); break;
            case "copynodesummary": CopyNodeSummaryToClipboard(); break;
            case "exit": _exitApplication(); break;
            default:
                if (action.StartsWith("session-reset|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("reset", action["session-reset|".Length..]);
                else if (action.StartsWith("session-compact|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("compact", action["session-compact|".Length..]);
                else if (action.StartsWith("session-delete|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("delete", action["session-delete|".Length..]);
                else if (action.StartsWith("session-thinking|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("thinking", split[2], split[1]);
                }
                else if (action.StartsWith("session-verbose|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("verbose", split[2], split[1]);
                }
                else if (action.StartsWith("session:", StringComparison.Ordinal))
                    OpenDashboard($"sessions/{action[8..]}");
                else if (action.StartsWith("dashboard:", StringComparison.Ordinal))
                    OpenDashboard(action["dashboard:".Length..]);
                else if (action.StartsWith("activity:", StringComparison.Ordinal))
                    _windowManager.ShowActivityStream(action["activity:".Length..]);
                else if (action.StartsWith("channel:", StringComparison.Ordinal))
                    ToggleChannel(action[8..]);
                else
                    // Default: treat as a Hub navigation tag (e.g. "nodes", "agent:main:sessions")
                    _windowManager.ShowHub(action);
                break;
        }
    }

    private void CopyDeviceIdToClipboard()
    {
        var nodeService = _nodeServiceProvider();
        if (nodeService?.FullDeviceId == null) return;

        try
        {
            var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(nodeService.FullDeviceId);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_DeviceIdCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_DeviceIdCopiedDetail"), nodeService.ShortDeviceId)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy device ID: {ex.Message}");
        }
    }

    private void CopyNodeSummaryToClipboard()
    {
        if (_appModel.Nodes.Length == 0) return;

        try
        {
            var lines = _appModel.Nodes.Select(node =>
            {
                var state = node.IsOnline ? "online" : "offline";
                var name = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;
                return $"{state}: {name} ({node.ShortId}) · {node.DetailText}";
            });
            var summary = string.Join(Environment.NewLine, lines);

            var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(summary);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_NodeSummaryCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_NodeSummaryCopiedDetail"), _appModel.Nodes.Length)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy node summary: {ex.Message}");
        }
    }

    private async Task ExecuteSessionActionAsync(string action, string sessionKey, string? value = null)
    {
        var client = _connectionManagerProvider()?.OperatorClient;
        if (client == null || string.IsNullOrWhiteSpace(sessionKey)) return;

        try
        {
            if (action is "reset" or "compact" or "delete")
            {
                var title = action switch
                {
                    "reset" => "Reset session?",
                    "compact" => "Compact session log?",
                    "delete" => "Delete session?",
                    _ => "Confirm session action"
                };
                var body = action switch
                {
                    "reset" => $"Start a fresh session for '{sessionKey}'?",
                    "compact" => $"Keep the latest log lines for '{sessionKey}' and archive the rest?",
                    "delete" => $"Delete '{sessionKey}' and archive its transcript?",
                    _ => "Continue?"
                };
                var button = action switch
                {
                    "reset" => "Reset",
                    "compact" => "Compact",
                    "delete" => "Delete",
                    _ => "Continue"
                };

                var confirmed = await ConfirmSessionActionAsync(title, body, button);
                if (!confirmed) return;
            }

            var sent = action switch
            {
                "reset" => await client.ResetSessionAsync(sessionKey),
                "compact" => await client.CompactSessionAsync(sessionKey, 400),
                "delete" => await client.DeleteSessionAsync(sessionKey, deleteTranscript: true),
                "thinking" => await client.PatchSessionAsync(sessionKey, thinkingLevel: value),
                "verbose" => await client.PatchSessionAsync(sessionKey, verboseLevel: value),
                _ => false
            };

            if (!sent)
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailedDetail")));
                return;
            }

            if (action is "thinking" or "verbose")
            {
                _ = client.RequestSessionsAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session action error ({action}): {ex.Message}");
            try
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(ex.Message));
            }
            catch { }
        }
    }

    private async Task<bool> ConfirmSessionActionAsync(string title, string body, string actionLabel)
    {
        var root = _keepAliveWindowProvider()?.Content as FrameworkElement;
        if (root?.XamlRoot == null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = actionLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    internal void OpenDashboard(string? path = null)
    {
        if (_settings == null) return;
        if (!_ensureSshTunnelConfigured()) return;

        var baseUrl = _settings.GetEffectiveGatewayUrl()
            .Replace("ws://", "http://")
            .Replace("wss://", "https://")
            .TrimEnd('/');

        var url = string.IsNullOrEmpty(path)
            ? baseUrl
            : $"{baseUrl}/{path.TrimStart('/')}";

        var activeToken = _gatewayRegistry?.GetActive()?.SharedGatewayToken;
        if (!string.IsNullOrEmpty(activeToken))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url = $"{url}{separator}token={Uri.EscapeDataString(activeToken)}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open dashboard: {ex.Message}");
        }
    }

    private async void ToggleChannel(string channelName)
    {
        var client = _connectionManagerProvider()?.OperatorClient;
        if (client == null) return;

        var channel = _appModel.Channels.FirstOrDefault(c => c.Name == channelName);
        if (channel == null) return;

        try
        {
            var isRunning = ChannelHealth.IsHealthyStatus(channel.Status);
            if (isRunning)
            {
                await client.StopChannelAsync(channelName);
                ActivityStreamService.Add(category: "channel", title: $"Stopped channel: {channelName}", dashboardPath: "settings");
            }
            else
            {
                await client.StartChannelAsync(channelName);
                ActivityStreamService.Add(category: "channel", title: $"Started channel: {channelName}", dashboardPath: "settings");
            }

            await _runHealthCheck();
        }
        catch (Exception ex)
        {
            ActivityStreamService.Add(category: "channel", title: $"Channel toggle failed: {channelName}", details: ex.Message);
            Logger.Error($"Failed to toggle channel: {ex.Message}");
        }
    }

    private void ToggleAutoStart()
    {
        if (_settings == null) return;
        _settings.AutoStart = !_settings.AutoStart;
        _settings.Save();
        AutoStartManager.SetAutoStart(_settings.AutoStart);
    }

    internal void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open log file: {ex.Message}");
        }
    }

    internal void OpenLogFolder()
    {
        OpenFolder(Path.GetDirectoryName(Logger.LogFilePath), "logs");
    }

    internal void OpenConfigFolder()
    {
        OpenFolder(SettingsManager.SettingsDirectoryPath, "config");
    }

    internal void OpenDiagnosticsFolder()
    {
        OpenFolder(Path.GetDirectoryName(DiagnosticsJsonlService.FilePath), "diagnostics");
    }

    private void RestartSshTunnel()
    {
        if (_settings?.UseSshTunnel != true)
        {
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Managed SSH tunnel mode is not enabled."));
            return;
        }

        try
        {
            Logger.Info("Restarting managed SSH tunnel from Command Center");
            DiagnosticsJsonlService.Write("tunnel.restart_requested", new
            {
                localEndpoint = _settings.SshTunnelLocalPort > 0 ? $"127.0.0.1:{_settings.SshTunnelLocalPort}" : null,
                remotePort = _settings.SshTunnelRemotePort
            });

            _sshTunnelService?.Stop();
            _updateTrayIcon();

            if (!_ensureSshTunnelConfigured())
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel restart failed")
                    .AddText(_sshTunnelService?.LastError ?? "Check SSH tunnel settings and logs."));
                return;
            }

            _ = _connectionManagerProvider()?.ReconnectAsync();

            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Restarted; reconnecting to gateway."));
        }
        catch (Exception ex)
        {
            Logger.Error($"SSH tunnel restart request failed: {ex.Message}");
            DiagnosticsJsonlService.Write("tunnel.restart_request_failed", new { ex.Message });
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel restart failed")
                .AddText(ex.Message));
        }
    }

    private static void OpenFolder(string? folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Logger.Warn($"Failed to open {label} folder: path is not configured");
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            Logger.Info($"Opened {label} folder: {folderPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Failed to open {label} folder {folderPath}: {ex.Message}");
        }
    }
}
