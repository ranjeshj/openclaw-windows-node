using OpenClaw.Shared;
using System;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Bridges gateway client events to the <see cref="AppModel"/>.
/// Owns the subscribe/unsubscribe lifecycle for all gateway client events, replacing
/// the fragile 27-event wire/unwire block that was in App.xaml.cs.
///
/// <para>Simple data-forwarding events are written directly to the model.
/// Complex events with UI side effects are re-raised for App to handle.</para>
/// </summary>
internal sealed class GatewayDataBridge
{
    private readonly AppModel _model;

    // ── Events re-raised for App to handle (complex side effects) ──

    public event EventHandler<ConnectionStatus>? ConnectionStatusChanged;
    public event EventHandler<string>? AuthenticationFailed;
    public event EventHandler<AgentActivity?>? ActivityChanged;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<OpenClawNotification>? NotificationReceived;
    public event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;

    public GatewayDataBridge(AppModel model)
    {
        _model = model;
    }

    /// <summary>
    /// Subscribes to all events on the new client, optionally unsubscribing from the old one.
    /// Replaces the 27-event wire/unwire block in App.OnOperatorClientChanged.
    /// </summary>
    public void Attach(IOperatorGatewayClient? newClient, IOperatorGatewayClient? oldClient = null, SettingsManager? settings = null)
    {
        if (oldClient is { } old)
        {
            old.StatusChanged -= OnConnectionStatusChanged;
            old.AuthenticationFailed -= OnAuthenticationFailed;
            old.ActivityChanged -= OnActivityChanged;
            old.NotificationReceived -= OnNotificationReceived;
            old.ChannelHealthUpdated -= OnChannelHealthUpdated;
            old.SessionsUpdated -= OnSessionsUpdated;
            old.UsageUpdated -= OnUsageUpdated;
            old.UsageStatusUpdated -= OnUsageStatusUpdated;
            old.UsageCostUpdated -= OnUsageCostUpdated;
            old.NodesUpdated -= OnNodesUpdated;
            old.SessionPreviewUpdated -= OnSessionPreviewUpdated;
            old.SessionCommandCompleted -= OnSessionCommandCompleted;
            old.GatewaySelfUpdated -= OnGatewaySelfUpdated;
            old.CronListUpdated -= OnCronListUpdated;
            old.CronStatusUpdated -= OnCronStatusUpdated;
            old.ConfigUpdated -= OnConfigUpdated;
            old.ConfigSchemaUpdated -= OnConfigSchemaUpdated;
            old.SkillsStatusUpdated -= OnSkillsStatusUpdated;
            old.AgentEventReceived -= OnAgentEventReceived;
            old.NodePairListUpdated -= OnNodePairListUpdated;
            old.DevicePairListUpdated -= OnDevicePairListUpdated;
            old.ModelsListUpdated -= OnModelsListUpdated;
            old.PresenceUpdated -= OnPresenceUpdated;
            old.AgentsListUpdated -= OnAgentsListUpdated;
            old.AgentFilesListUpdated -= OnAgentFilesListUpdated;
            old.AgentFileContentUpdated -= OnAgentFileContentUpdated;
        }

        if (newClient is { } client)
        {
            client.SetUserRules(settings?.UserRules?.Count > 0 ? settings.UserRules : null);
            client.SetPreferStructuredCategories(settings?.PreferStructuredCategories ?? true);
            client.StatusChanged += OnConnectionStatusChanged;
            client.AuthenticationFailed += OnAuthenticationFailed;
            client.ActivityChanged += OnActivityChanged;
            client.NotificationReceived += OnNotificationReceived;
            client.ChannelHealthUpdated += OnChannelHealthUpdated;
            client.SessionsUpdated += OnSessionsUpdated;
            client.UsageUpdated += OnUsageUpdated;
            client.UsageStatusUpdated += OnUsageStatusUpdated;
            client.UsageCostUpdated += OnUsageCostUpdated;
            client.NodesUpdated += OnNodesUpdated;
            client.SessionPreviewUpdated += OnSessionPreviewUpdated;
            client.SessionCommandCompleted += OnSessionCommandCompleted;
            client.GatewaySelfUpdated += OnGatewaySelfUpdated;
            client.CronListUpdated += OnCronListUpdated;
            client.CronStatusUpdated += OnCronStatusUpdated;
            client.ConfigUpdated += OnConfigUpdated;
            client.ConfigSchemaUpdated += OnConfigSchemaUpdated;
            client.SkillsStatusUpdated += OnSkillsStatusUpdated;
            client.AgentEventReceived += OnAgentEventReceived;
            client.NodePairListUpdated += OnNodePairListUpdated;
            client.DevicePairListUpdated += OnDevicePairListUpdated;
            client.ModelsListUpdated += OnModelsListUpdated;
            client.PresenceUpdated += OnPresenceUpdated;
            client.AgentsListUpdated += OnAgentsListUpdated;
            client.AgentFilesListUpdated += OnAgentFilesListUpdated;
            client.AgentFileContentUpdated += OnAgentFileContentUpdated;
        }

        _model.GatewaySelf = null;
    }

    // ── Complex handlers: re-raise for App ──

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
        => ConnectionStatusChanged?.Invoke(sender, status);

    private void OnAuthenticationFailed(object? sender, string message)
        => AuthenticationFailed?.Invoke(sender, message);

    private void OnActivityChanged(object? sender, AgentActivity? activity)
        => ActivityChanged?.Invoke(sender, activity);

    private void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
        => ChannelHealthUpdated?.Invoke(sender, channels);

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
        => SessionsUpdated?.Invoke(sender, sessions);

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo cost)
        => UsageCostUpdated?.Invoke(sender, cost);

    private void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo self)
        => GatewaySelfUpdated?.Invoke(sender, self);

    private void OnNodesUpdated(object? sender, GatewayNodeInfo[] nodes)
        => NodesUpdated?.Invoke(sender, nodes);

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
        => SessionCommandCompleted?.Invoke(sender, result);

    private void OnNotificationReceived(object? sender, OpenClawNotification notification)
        => NotificationReceived?.Invoke(sender, notification);

    private void OnSessionPreviewUpdated(object? sender, SessionsPreviewPayloadInfo payload)
        => SessionPreviewUpdated?.Invoke(sender, payload);

    // ── Simple data handlers: cache in model ──

    private void OnUsageUpdated(object? sender, GatewayUsageInfo usage)
        => _model.Usage = usage;

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
        => _model.UsageStatus = usageStatus;

    private void OnCronListUpdated(object? sender, JsonElement data)
        => _model.CronList = data.Clone();

    private void OnCronStatusUpdated(object? sender, JsonElement data)
        => _model.CronStatus = data.Clone();

    private void OnSkillsStatusUpdated(object? sender, JsonElement data)
        => _model.SkillsStatus = data.Clone();

    private void OnConfigUpdated(object? sender, JsonElement data)
        => _model.Config = data.Clone();

    private void OnConfigSchemaUpdated(object? sender, JsonElement data)
        => _model.ConfigSchema = data.Clone();

    private void OnAgentsListUpdated(object? sender, JsonElement data)
        => _model.AgentsList = data.Clone();

    private void OnAgentFilesListUpdated(object? sender, JsonElement data)
        => _model.AgentFilesList = data.Clone();

    private void OnAgentFileContentUpdated(object? sender, JsonElement data)
        => _model.AgentFileContent = data.Clone();

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
        => _model.AddAgentEvent(evt);

    private void OnNodePairListUpdated(object? sender, PairingListInfo data)
        => _model.NodePairList = data;

    private void OnDevicePairListUpdated(object? sender, DevicePairingListInfo data)
        => _model.DevicePairList = data;

    private void OnModelsListUpdated(object? sender, ModelsListInfo data)
        => _model.ModelsList = data;

    private void OnPresenceUpdated(object? sender, PresenceEntry[] data)
        => _model.Presence = data;
}
