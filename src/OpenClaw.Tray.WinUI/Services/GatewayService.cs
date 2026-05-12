using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// Owns gateway event subscriptions and data-side handling for operator
/// gateway client events. UI-only concerns (toasts, windows, tray icon,
/// voice overlay) are re-raised as events for App to handle.
/// </summary>
internal sealed class GatewayService
{
    private static readonly TimeSpan SessionSwitchDebounce = TimeSpan.FromSeconds(3);

    private readonly AppState _state;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IOperatorGatewayClient?> _operatorClientProvider;

    // Session-aware activity tracking
    private readonly Dictionary<string, AgentActivity> _sessionActivities = new();
    private string? _displayedSessionKey;
    private DateTime _lastSessionSwitch = DateTime.MinValue;
    private DateTime _lastPreviewRequestUtc = DateTime.MinValue;
    private DateTime _lastCheckTime = DateTime.Now;
    private DateTime _lastUsageActivityLogUtc = DateTime.MinValue;
    private string? _lastChannelStatusSignature;

    /// <summary>Last gateway health-check time, read by <see cref="CommandCenterBuilder"/>.</summary>
    public DateTime LastCheckTime
    {
        get => _lastCheckTime;
        set => _lastCheckTime = value;
    }

    // ── Re-raised events for App (UI concerns) ──

    /// <summary>Raised after data-side handling of StatusChanged. App handles: clear hub auth error, update tray icon, run health check.</summary>
    public event EventHandler<ConnectionStatus>? ConnectionStatusChanged;

    /// <summary>Raised after data-side handling of AuthenticationFailed. App handles: update hub auth error, update tray icon.</summary>
    public event EventHandler<string>? AuthenticationFailed;

    /// <summary>Raised when a session command completes. App handles: show toast, request sessions refresh.</summary>
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;

    /// <summary>Raised when a notification is received. App handles: voice overlay, TTS, show toast with filtering.</summary>
    public event EventHandler<OpenClawNotification>? NotificationReceived;

    public GatewayService(
        AppState state,
        DispatcherQueue dispatcher,
        Func<IOperatorGatewayClient?> operatorClientProvider)
    {
        _state = state;
        _dispatcher = dispatcher;
        _operatorClientProvider = operatorClientProvider;
    }

    // ── Attach / Detach ──

    /// <summary>
    /// Subscribes to all gateway client events on <paramref name="newClient"/> and
    /// unsubscribes from <paramref name="oldClient"/>. Also configures user rules
    /// and structured-category preference on the new client.
    /// </summary>
    public void Attach(
        IOperatorGatewayClient? newClient,
        IOperatorGatewayClient? oldClient,
        SettingsManager? settings)
    {
        if (oldClient is not null)
        {
            oldClient.StatusChanged -= OnConnectionStatusChanged;
            oldClient.AuthenticationFailed -= OnAuthenticationFailed;
            oldClient.ActivityChanged -= OnActivityChanged;
            oldClient.NotificationReceived -= OnNotificationReceived;
            oldClient.ChannelHealthUpdated -= OnChannelHealthUpdated;
            oldClient.SessionsUpdated -= OnSessionsUpdated;
            oldClient.UsageUpdated -= OnUsageUpdated;
            oldClient.UsageStatusUpdated -= OnUsageStatusUpdated;
            oldClient.UsageCostUpdated -= OnUsageCostUpdated;
            oldClient.NodesUpdated -= OnNodesUpdated;
            oldClient.SessionPreviewUpdated -= OnSessionPreviewUpdated;
            oldClient.SessionCommandCompleted -= OnSessionCommandCompleted;
            oldClient.GatewaySelfUpdated -= OnGatewaySelfUpdated;
            oldClient.CronListUpdated -= OnCronListUpdated;
            oldClient.CronStatusUpdated -= OnCronStatusUpdated;
            oldClient.ConfigUpdated -= OnConfigUpdated;
            oldClient.ConfigSchemaUpdated -= OnConfigSchemaUpdated;
            oldClient.SkillsStatusUpdated -= OnSkillsStatusUpdated;
            oldClient.AgentEventReceived -= OnAgentEventReceived;
            oldClient.NodePairListUpdated -= OnNodePairListUpdated;
            oldClient.DevicePairListUpdated -= OnDevicePairListUpdated;
            oldClient.ModelsListUpdated -= OnModelsListUpdated;
            oldClient.PresenceUpdated -= OnPresenceUpdated;
            oldClient.AgentsListUpdated -= OnAgentsListUpdated;
            oldClient.AgentFilesListUpdated -= OnAgentFilesListUpdated;
            oldClient.AgentFileContentUpdated -= OnAgentFileContentUpdated;
        }

        if (newClient is not null)
        {
            newClient.SetUserRules(settings?.UserRules?.Count > 0 ? settings.UserRules : null);
            newClient.SetPreferStructuredCategories(settings?.PreferStructuredCategories ?? true);

            newClient.StatusChanged += OnConnectionStatusChanged;
            newClient.AuthenticationFailed += OnAuthenticationFailed;
            newClient.ActivityChanged += OnActivityChanged;
            newClient.NotificationReceived += OnNotificationReceived;
            newClient.ChannelHealthUpdated += OnChannelHealthUpdated;
            newClient.SessionsUpdated += OnSessionsUpdated;
            newClient.UsageUpdated += OnUsageUpdated;
            newClient.UsageStatusUpdated += OnUsageStatusUpdated;
            newClient.UsageCostUpdated += OnUsageCostUpdated;
            newClient.NodesUpdated += OnNodesUpdated;
            newClient.SessionPreviewUpdated += OnSessionPreviewUpdated;
            newClient.SessionCommandCompleted += OnSessionCommandCompleted;
            newClient.GatewaySelfUpdated += OnGatewaySelfUpdated;
            newClient.CronListUpdated += OnCronListUpdated;
            newClient.CronStatusUpdated += OnCronStatusUpdated;
            newClient.ConfigUpdated += OnConfigUpdated;
            newClient.ConfigSchemaUpdated += OnConfigSchemaUpdated;
            newClient.SkillsStatusUpdated += OnSkillsStatusUpdated;
            newClient.AgentEventReceived += OnAgentEventReceived;
            newClient.NodePairListUpdated += OnNodePairListUpdated;
            newClient.DevicePairListUpdated += OnDevicePairListUpdated;
            newClient.ModelsListUpdated += OnModelsListUpdated;
            newClient.PresenceUpdated += OnPresenceUpdated;
            newClient.AgentsListUpdated += OnAgentsListUpdated;
            newClient.AgentFilesListUpdated += OnAgentFilesListUpdated;
            newClient.AgentFileContentUpdated += OnAgentFileContentUpdated;
        }

        EnqueueModelUpdate(() => _state.GatewaySelf = null);
    }

    // ── Dispatcher helper ──

    private void EnqueueModelUpdate(Action update)
    {
        if (_dispatcher.HasThreadAccess) update();
        else _dispatcher.TryEnqueue(() => update());
    }

    // ── Complex handlers (data + logging, no UI) ──

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        DiagnosticsJsonlService.Write("connection.status", new
        {
            status = status.ToString(),
        });
        EnqueueModelUpdate(() =>
        {
            if (status == ConnectionStatus.Connected)
            {
                _state.AuthFailureMessage = null;
            }

            if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Error)
            {
                _state.ClearCachedData();
            }
        });

        // Re-raise for App to handle UI concerns (tray icon, hub window, health check)
        ConnectionStatusChanged?.Invoke(this, status);
    }

    private void OnAuthenticationFailed(object? sender, string message)
    {
        Logger.Error($"Authentication failed: {message}");
        DiagnosticsJsonlService.Write("connection.auth_failed", new { message });
        ActivityStreamService.Add(category: "error", title: $"Auth failed: {message}");
        EnqueueModelUpdate(() =>
        {
            _state.AuthFailureMessage = message;
        });

        // Re-raise for App to handle UI concerns (hub auth error, tray icon)
        AuthenticationFailed?.Invoke(this, message);
    }

    private void OnActivityChanged(object? sender, AgentActivity? activity)
    {
        if (activity == null)
        {
            if (_displayedSessionKey != null && _sessionActivities.ContainsKey(_displayedSessionKey))
            {
                _sessionActivities.Remove(_displayedSessionKey);
            }
            EnqueueModelUpdate(() => _state.CurrentActivity = null);
        }
        else
        {
            var sessionKey = activity.SessionKey ?? "default";
            _sessionActivities[sessionKey] = activity;
            ActivityStreamService.Add(
                category: "session",
                title: $"{sessionKey}: {activity.Label}",
                dashboardPath: $"sessions/{sessionKey}",
                details: activity.Kind.ToString(),
                sessionKey: sessionKey);

            var now = DateTime.Now;
            if (_displayedSessionKey != sessionKey &&
                (now - _lastSessionSwitch) > SessionSwitchDebounce)
            {
                _displayedSessionKey = sessionKey;
                _lastSessionSwitch = now;
            }

            if (_displayedSessionKey == sessionKey)
            {
                EnqueueModelUpdate(() => _state.CurrentActivity = activity);
            }
        }
    }

    // OnChannelHealthUpdated and OnGatewaySelfUpdated are internal so NodeService
    // events (which share the same signature) can also be wired to them.
    internal void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
    {
        _lastCheckTime = DateTime.Now;
        var signature = string.Join("|", channels
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Name}:{c.Status}:{c.Error}"));
        if (!string.Equals(signature, _lastChannelStatusSignature, StringComparison.Ordinal))
        {
            _lastChannelStatusSignature = signature;
            var summary = channels.Length == 0
                ? "No channels reported"
                : string.Join(", ", channels.Select(c => $"{c.Name}={c.Status}"));
            DiagnosticsJsonlService.Write("gateway.health.channels", new
            {
                channelCount = channels.Length,
                healthyCount = channels.Count(c => ChannelHealth.IsHealthyStatus(c.Status)),
                errorCount = channels.Count(c => !string.IsNullOrWhiteSpace(c.Error))
            });
            ActivityStreamService.Add(
                category: "channel",
                title: "Channel health updated",
                dashboardPath: "channels",
                details: summary);
        }
        EnqueueModelUpdate(() => _state.Channels = channels);
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        EnqueueModelUpdate(() =>
        {
            _state.Sessions = sessions;

            var activeKeys = new HashSet<string>(sessions.Select(s => s.Key), StringComparer.Ordinal);
            _state.PruneSessionPreviews(activeKeys);
        });

        var client = _operatorClientProvider();
        if (client != null &&
            sessions.Length > 0 &&
            DateTime.UtcNow - _lastPreviewRequestUtc > TimeSpan.FromSeconds(5))
        {
            _lastPreviewRequestUtc = DateTime.UtcNow;
            var keys = sessions.Take(5).Select(s => s.Key).ToArray();
            _ = client.RequestSessionPreviewAsync(keys, limit: 3, maxChars: 140);
        }
    }

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo usageCost)
    {
        EnqueueModelUpdate(() => _state.UsageCost = usageCost);

        if (DateTime.UtcNow - _lastUsageActivityLogUtc > TimeSpan.FromMinutes(1))
        {
            _lastUsageActivityLogUtc = DateTime.UtcNow;
            ActivityStreamService.Add(
                category: "usage",
                title: $"{usageCost.Days}d usage ${usageCost.Totals.TotalCost:F2}",
                dashboardPath: "usage",
                details: $"{usageCost.Totals.TotalTokens:N0} tokens");
        }
    }

    internal void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo gatewaySelf)
    {
        EnqueueModelUpdate(() =>
        {
            _state.GatewaySelf = _state.GatewaySelf?.Merge(gatewaySelf) ?? gatewaySelf;
            DiagnosticsJsonlService.Write("gateway.self", new
            {
                version = _state.GatewaySelf.ServerVersion,
                protocol = _state.GatewaySelf.Protocol,
                uptimeMs = _state.GatewaySelf.UptimeMs,
                authMode = _state.GatewaySelf.AuthMode,
                stateVersionPresence = _state.GatewaySelf.StateVersionPresence,
                stateVersionHealth = _state.GatewaySelf.StateVersionHealth,
                presenceCount = _state.GatewaySelf.PresenceCount
            });
        });
    }

    private void OnNodesUpdated(object? sender, GatewayNodeInfo[] nodes)
    {
        var previousCount = _state.Nodes.Length;
        var previousOnline = _state.Nodes.Count(n => n.IsOnline);
        var online = nodes.Count(n => n.IsOnline);
        EnqueueModelUpdate(() => _state.Nodes = nodes);

        if (nodes.Length != previousCount || online != previousOnline)
        {
            ActivityStreamService.Add(
                category: "node",
                title: $"Nodes {online}/{nodes.Length} online",
                dashboardPath: "nodes");
        }
    }

    private void OnSessionPreviewUpdated(object? sender, SessionsPreviewPayloadInfo payload)
    {
        EnqueueModelUpdate(() =>
        {
            foreach (var preview in payload.Previews)
            {
                _state.SetSessionPreview(preview.Key, preview);
            }
        });
    }

    // ── Re-raised complex handlers (App handles all UI) ──

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        SessionCommandCompleted?.Invoke(this, result);
    }

    private void OnNotificationReceived(object? sender, OpenClawNotification notification)
    {
        ActivityStreamService.Add(
            category: "notification",
            title: $"{notification.Type ?? "info"}: {notification.Title ?? "notification"}",
            details: notification.Message);

        NotificationReceived?.Invoke(this, notification);
    }

    // ── Simple data handlers ──

    private void OnUsageUpdated(object? sender, GatewayUsageInfo usage)
        => EnqueueModelUpdate(() => _state.Usage = usage);

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
        => EnqueueModelUpdate(() => _state.UsageStatus = usageStatus);

    private void OnCronListUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.CronList = clone); }

    private void OnCronStatusUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.CronStatus = clone); }

    private void OnSkillsStatusUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.SkillsStatus = clone); }

    private void OnConfigUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.Config = clone); }

    private void OnConfigSchemaUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.ConfigSchema = clone); }

    private void OnAgentsListUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.AgentsList = clone); }

    private void OnAgentFilesListUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.AgentFilesList = clone); }

    private void OnAgentFileContentUpdated(object? sender, JsonElement data)
    { var clone = data.Clone(); EnqueueModelUpdate(() => _state.AgentFileContent = clone); }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
        => EnqueueModelUpdate(() => _state.AddAgentEvent(evt));

    private void OnNodePairListUpdated(object? sender, PairingListInfo data)
        => EnqueueModelUpdate(() => _state.NodePairList = data);

    private void OnDevicePairListUpdated(object? sender, DevicePairingListInfo data)
        => EnqueueModelUpdate(() => _state.DevicePairList = data);

    private void OnModelsListUpdated(object? sender, ModelsListInfo data)
        => EnqueueModelUpdate(() => _state.ModelsList = data);

    private void OnPresenceUpdated(object? sender, PresenceEntry[] data)
        => EnqueueModelUpdate(() => _state.Presence = data);
}
