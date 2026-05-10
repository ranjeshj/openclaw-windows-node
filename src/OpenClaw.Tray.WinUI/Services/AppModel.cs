using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Observable application state model. Holds all cached gateway data as the
/// single source of truth. Replaces the scattered <c>_last*</c> fields that
/// were previously stored in both App.xaml.cs and HubWindow.
///
/// <para><b>Threading invariant:</b> All property writes MUST happen on the
/// UI dispatcher thread. Background event handlers in App should enqueue
/// updates via <c>DispatcherQueue.TryEnqueue</c>.</para>
///
/// <para>Pages and windows read from this model. During the transition
/// period, HubWindow delegates its <c>Last*</c> properties to this model.</para>
/// </summary>
internal sealed class AppModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Connection State ──

    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    public ConnectionStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private string? _authFailureMessage;
    public string? AuthFailureMessage
    {
        get => _authFailureMessage;
        set => SetField(ref _authFailureMessage, value);
    }

    private AgentActivity? _currentActivity;
    public AgentActivity? CurrentActivity
    {
        get => _currentActivity;
        set => SetField(ref _currentActivity, value);
    }

    // ── Gateway Data ──

    private ChannelHealth[] _channels = Array.Empty<ChannelHealth>();
    public ChannelHealth[] Channels
    {
        get => _channels;
        set => SetField(ref _channels, value);
    }

    private SessionInfo[] _sessions = Array.Empty<SessionInfo>();
    public SessionInfo[] Sessions
    {
        get => _sessions;
        set => SetField(ref _sessions, value);
    }

    private GatewayNodeInfo[] _nodes = Array.Empty<GatewayNodeInfo>();
    public GatewayNodeInfo[] Nodes
    {
        get => _nodes;
        set => SetField(ref _nodes, value);
    }

    private GatewayUsageInfo? _usage;
    public GatewayUsageInfo? Usage
    {
        get => _usage;
        set => SetField(ref _usage, value);
    }

    private GatewayUsageStatusInfo? _usageStatus;
    public GatewayUsageStatusInfo? UsageStatus
    {
        get => _usageStatus;
        set => SetField(ref _usageStatus, value);
    }

    private GatewayCostUsageInfo? _usageCost;
    public GatewayCostUsageInfo? UsageCost
    {
        get => _usageCost;
        set => SetField(ref _usageCost, value);
    }

    private GatewaySelfInfo? _gatewaySelf;
    public GatewaySelfInfo? GatewaySelf
    {
        get => _gatewaySelf;
        set => SetField(ref _gatewaySelf, value);
    }

    private PairingListInfo? _nodePairList;
    public PairingListInfo? NodePairList
    {
        get => _nodePairList;
        set => SetField(ref _nodePairList, value);
    }

    private DevicePairingListInfo? _devicePairList;
    public DevicePairingListInfo? DevicePairList
    {
        get => _devicePairList;
        set => SetField(ref _devicePairList, value);
    }

    private ModelsListInfo? _modelsList;
    public ModelsListInfo? ModelsList
    {
        get => _modelsList;
        set => SetField(ref _modelsList, value);
    }

    private PresenceEntry[]? _presence;
    public PresenceEntry[]? Presence
    {
        get => _presence;
        set => SetField(ref _presence, value);
    }

    private JsonElement? _agentsList;
    public JsonElement? AgentsList
    {
        get => _agentsList;
        set => SetField(ref _agentsList, value);
    }

    private JsonElement? _config;
    public JsonElement? Config
    {
        get => _config;
        set => SetField(ref _config, value);
    }

    private JsonElement? _configSchema;
    public JsonElement? ConfigSchema
    {
        get => _configSchema;
        set => SetField(ref _configSchema, value);
    }

    private JsonElement? _cronList;
    public JsonElement? CronList
    {
        get => _cronList;
        set => SetField(ref _cronList, value);
    }

    private JsonElement? _cronStatus;
    public JsonElement? CronStatus
    {
        get => _cronStatus;
        set => SetField(ref _cronStatus, value);
    }

    private JsonElement? _skillsStatus;
    public JsonElement? SkillsStatus
    {
        get => _skillsStatus;
        set => SetField(ref _skillsStatus, value);
    }

    private JsonElement? _agentFilesList;
    public JsonElement? AgentFilesList
    {
        get => _agentFilesList;
        set => SetField(ref _agentFilesList, value);
    }

    private JsonElement? _agentFileContent;
    public JsonElement? AgentFileContent
    {
        get => _agentFileContent;
        set => SetField(ref _agentFileContent, value);
    }

    private UpdateCommandCenterInfo _updateInfo= new()
    {
        Status = "Not checked",
        CurrentVersion = typeof(AppModel).Assembly.GetName().Version?.ToString() ?? "unknown"
    };
    public UpdateCommandCenterInfo UpdateInfo
    {
        get => _updateInfo;
        set => SetField(ref _updateInfo, value);
    }

    // ── Session Previews (thread-safe via lock) ──

    private readonly Dictionary<string, SessionPreviewInfo> _sessionPreviews = new();
    private readonly object _sessionPreviewsLock = new();

    public SessionPreviewInfo? GetSessionPreview(string key)
    {
        lock (_sessionPreviewsLock)
            return _sessionPreviews.TryGetValue(key, out var p) ? p : null;
    }

    public void SetSessionPreview(string key, SessionPreviewInfo preview)
    {
        lock (_sessionPreviewsLock)
            _sessionPreviews[key] = preview;
    }

    public void PruneSessionPreviews(HashSet<string> validKeys)
    {
        lock (_sessionPreviewsLock)
        {
            var stale = new List<string>();
            foreach (var k in _sessionPreviews.Keys)
                if (!validKeys.Contains(k)) stale.Add(k);
            foreach (var k in stale)
                _sessionPreviews.Remove(k);
        }
    }

    // ── Agent Events Cache ──

    private readonly List<AgentEventInfo> _agentEvents = new();
    private const int MaxAgentEvents = 400;

    public IReadOnlyList<AgentEventInfo> AgentEvents
    {
        get
        {
            lock (_agentEvents) return _agentEvents.ToArray();
        }
    }

    public event Action<AgentEventInfo>? AgentEventAdded;

    public void AddAgentEvent(AgentEventInfo evt)
    {
        lock (_agentEvents)
        {
            _agentEvents.Add(evt);
            if (_agentEvents.Count > MaxAgentEvents)
                _agentEvents.RemoveAt(0);
        }
        AgentEventAdded?.Invoke(evt);
    }

    public void ClearAgentEvents()
    {
        lock (_agentEvents) _agentEvents.Clear();
    }

    // ── Helpers ──

    /// <summary>
    /// Resets all cached data to defaults. Called on disconnect.
    /// </summary>
    public void ClearCachedData()
    {
        Sessions = Array.Empty<SessionInfo>();
        Nodes = Array.Empty<GatewayNodeInfo>();
        Channels = Array.Empty<ChannelHealth>();
        NodePairList = null;
        DevicePairList = null;
        ModelsList = null;
        GatewaySelf = null;
        Usage = null;
        UsageStatus = null;
        UsageCost = null;
        Presence = null;
        AgentsList = null;
        Config = null;
        ConfigSchema = null;
        CronList = null;
        CronStatus = null;
        SkillsStatus = null;
        AgentFilesList = null;
        AgentFileContent = null;
        AuthFailureMessage = null;
        CurrentActivity = null;
        ClearAgentEvents();
        lock (_sessionPreviewsLock) _sessionPreviews.Clear();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
