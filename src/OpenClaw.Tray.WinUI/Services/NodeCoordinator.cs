using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClawTray.Windows;
using System;

namespace OpenClawTray.Services;

/// <summary>
/// Handles node-related events (status, pairing, recording, invokes, notifications)
/// that were previously embedded in App.xaml.cs. Wired to <see cref="NodeService"/>
/// events by App after construction.
/// </summary>
internal sealed class NodeCoordinator
{
    private readonly ToastService? _toastService;
    private readonly SettingsManager? _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<HubWindow?> _hubWindowProvider;
    private readonly Func<LocalGatewaySetupEngine?> _localSetupEngineProvider;
    private readonly Action _updateTrayIcon;

    public NodeCoordinator(
        ToastService? toastService,
        SettingsManager? settings,
        DispatcherQueue dispatcher,
        Func<HubWindow?> hubWindowProvider,
        Func<LocalGatewaySetupEngine?> localSetupEngineProvider,
        Action updateTrayIcon)
    {
        _toastService = toastService;
        _settings = settings;
        _dispatcher = dispatcher;
        _hubWindowProvider = hubWindowProvider;
        _localSetupEngineProvider = localSetupEngineProvider;
        _updateTrayIcon = updateTrayIcon;
    }

    public void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        ActivityStreamService.Add(category: "node", title: $"Node mode {status}", dashboardPath: "nodes");

        if (_settings?.EnableNodeMode == true)
        {
            _updateTrayIcon();
        }

        SyncHubNodeState(sender as NodeService);

        var nodeService = sender as NodeService;
        if (status == ConnectionStatus.Connected && nodeService?.IsPaired == true)
        {
            var deviceId = nodeService.FullDeviceId;
            if (_toastService?.HasRecentToast("node-paired", deviceId) == true)
            {
                Logger.Info($"[ToastDeduper] Suppressed node-connected toast after node-paired deviceId={deviceId}");
                return;
            }

            try
            {
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActive"))
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActiveDetail")),
                    "node-connected",
                    deviceId);
            }
            catch { /* ignore */ }
        }
    }

    public void OnRecordingStateChanged(object? sender, RecordingStateEventArgs args)
    {
        var source = args.Type == RecordingType.Screen ? "Screen" : "Camera";
        if (args.IsActive)
        {
            var title = args.Type == RecordingType.Screen
                ? LocalizationHelper.GetString("Activity_ScreenRecordingStarted")
                : LocalizationHelper.GetString("Activity_CameraRecordingStarted");
            var duration = args.DurationMs > 0 ? $" ({args.DurationMs / 1000.0:0.#}s)" : "";
            ActivityStreamService.Add(category: "node", title: $"{title}{duration}",
                icon: "🔴",
                details: string.Format(LocalizationHelper.GetString("Activity_RecordingRequestedByAgent"), source));
        }
        else
        {
            var title = args.Type == RecordingType.Screen
                ? LocalizationHelper.GetString("Activity_ScreenRecordingComplete")
                : LocalizationHelper.GetString("Activity_CameraRecordingComplete");
            ActivityStreamService.Add(category: "node", title: title,
                icon: "✅",
                details: string.Format(LocalizationHelper.GetString("Activity_RecordingSentToAgent"), source));
        }
    }

    public void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");

        try
        {
            if (args.Status == PairingStatus.Pending)
            {
                if (LocalGatewaySetupEngine.ShouldSuppressPairingPendingNotification(_localSetupEngineProvider(), args.Status))
                {
                    Logger.Info($"Suppressing pairing-pending toast: autopair Phase 14 in progress for {args.DeviceId}");
                    return;
                }
                ShowPairingPendingNotification(args.DeviceId);
            }
            else if (args.Status == PairingStatus.Paired)
            {
                var deviceKey = args.DeviceId ?? string.Empty;
                if (_toastService?.TryMarkPairedToastShown(deviceKey) == true)
                {
                    ActivityStreamService.Add(category: "node", title: "Node paired", dashboardPath: "nodes", nodeId: args.DeviceId);
                    _toastService?.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_NodePaired"))
                        .AddText(LocalizationHelper.GetString("Toast_NodePairedDetail")),
                        "node-paired",
                        args.DeviceId);
                }
                else
                {
                    Logger.Info($"Suppressing duplicate Paired toast for device {deviceKey}");
                }
            }
            else if (args.Status == PairingStatus.Rejected)
            {
                ActivityStreamService.Add(category: "node", title: "Node pairing rejected", dashboardPath: "nodes", nodeId: args.DeviceId, details: args.Message ?? LocalizationHelper.GetString("Toast_PairingRejectedDetail"));
                _toastService?.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejected"))
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejectedDetail")),
                    "node-pairing-rejected",
                    args.DeviceId);
            }
        }
        catch { /* ignore */ }

        SyncHubNodeState(sender as NodeService);
    }

    /// <summary>
    /// Pushes current node service state to hub window so ConnectionPage reflects live pairing/identity.
    /// </summary>
    public void SyncHubNodeState(NodeService? nodeService)
    {
        var hub = _hubWindowProvider();
        if (hub == null || hub.IsClosed) return;
        if (nodeService != null)
        {
            hub.NodeIsConnected = nodeService.IsConnected;
            hub.NodeIsPaired = nodeService.IsPaired;
            hub.NodeIsPendingApproval = nodeService.IsPendingApproval;
            hub.NodeShortDeviceId = nodeService.ShortDeviceId;
            hub.NodeFullDeviceId = nodeService.FullDeviceId;
            hub.VoiceServiceInstance = nodeService.VoiceService;
        }
        else
        {
            hub.NodeIsConnected = false;
            hub.NodeIsPaired = false;
            hub.NodeIsPendingApproval = false;
        }
    }

    public static string BuildPairingApprovalCommand(string deviceId) =>
        $"openclaw devices approve {deviceId}";

    public void ShowPairingPendingNotification(string deviceId, string? approvalCommand = null)
    {
        var command = approvalCommand ?? BuildPairingApprovalCommand(deviceId);
        var shortDeviceId = deviceId.Length > 16 ? deviceId[..16] : deviceId;

        ActivityStreamService.Add(category: "node", title: "Node pairing pending", dashboardPath: "nodes", nodeId: deviceId);
        _toastService?.ShowToast(new ToastContentBuilder()
            .AddText(LocalizationHelper.GetString("Toast_PairingPending"))
            .AddText(string.Format(LocalizationHelper.GetString("Toast_PairingPendingDetail"), shortDeviceId))
            .AddButton(new ToastButton()
                .SetContent(LocalizationHelper.GetString("Toast_CopyPairingCommand"))
                .AddArgument("action", "copy_pairing_command")
                .AddArgument("command", command)),
            "node-pairing-pending",
            deviceId);
    }

    public void OnNodeNotificationRequested(object? sender, SystemNotifyArgs args)
    {
        ActivityStreamService.Add(category: "node", title: args.Title, dashboardPath: "nodes", details: args.Body);

        try
        {
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(args.Title)
                .AddText(args.Body));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show node notification: {ex.Message}");
        }
    }

    public void OnNodeInvokeCompleted(object? sender, NodeInvokeCompletedEventArgs args)
    {
        var status = args.Ok ? "completed" : "failed";
        var durationMs = Math.Max(0, (int)Math.Round(args.Duration.TotalMilliseconds));
        var details = args.Ok
            ? $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms"
            : $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms · {args.Error ?? "unknown error"}";

        ActivityStreamService.Add(
            category: "node.invoke",
            title: $"node.invoke {status}: {args.Command}",
            dashboardPath: "nodes",
            details: details,
            nodeId: args.NodeId);
    }

    internal static string GetNodeInvokePrivacyClass(string command)
    {
        if (string.Equals(command, "screen.record", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "screen.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.snap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.clip", StringComparison.OrdinalIgnoreCase))
        {
            return "privacy-sensitive";
        }

        if (command.StartsWith("system.run", StringComparison.OrdinalIgnoreCase))
        {
            return "exec";
        }

        return "metadata";
    }
}
