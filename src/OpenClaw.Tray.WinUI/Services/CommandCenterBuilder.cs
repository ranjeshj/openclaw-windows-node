using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Builds <see cref="GatewayCommandCenterState"/> snapshots from cached app
/// model data, settings, and runtime services.  Extracted from App.xaml.cs.
/// </summary>
internal sealed class CommandCenterBuilder
{
    private readonly AppModel _model;
    private readonly SettingsManager? _settings;
    private readonly Func<GatewayNodeInfo?> _localNodeProvider;
    private readonly Func<bool> _nodeIsPendingApproval;
    private readonly Func<string?> _nodeFullDeviceId;
    private readonly Func<string?> _nodeShortDeviceId;
    private readonly SshTunnelService? _sshTunnelService;
    private readonly Func<DateTime> _lastCheckTimeProvider;
    private readonly Func<IOperatorGatewayClient?> _operatorClientProvider;

    public CommandCenterBuilder(
        AppModel model,
        SettingsManager? settings,
        Func<GatewayNodeInfo?> localNodeProvider,
        Func<bool> nodeIsPendingApproval,
        Func<string?> nodeFullDeviceId,
        Func<string?> nodeShortDeviceId,
        SshTunnelService? sshTunnelService,
        Func<DateTime> lastCheckTimeProvider,
        Func<IOperatorGatewayClient?> operatorClientProvider)
    {
        _model = model;
        _settings = settings;
        _localNodeProvider = localNodeProvider;
        _nodeIsPendingApproval = nodeIsPendingApproval;
        _nodeFullDeviceId = nodeFullDeviceId;
        _nodeShortDeviceId = nodeShortDeviceId;
        _sshTunnelService = sshTunnelService;
        _lastCheckTimeProvider = lastCheckTimeProvider;
        _operatorClientProvider = operatorClientProvider;
    }

    public GatewayCommandCenterState Build()
    {
        var nodes = _model.Nodes.Select(NodeCapabilityHealthInfo.FromNode).ToList();
        if (nodes.Count == 0 && _localNodeProvider() is { } localNode)
        {
            nodes.Add(NodeCapabilityHealthInfo.FromNode(localNode));
        }

        var topology = GatewayTopologyClassifier.Classify(
            _settings?.GatewayUrl,
            _settings?.UseSshTunnel == true,
            _settings?.SshTunnelHost,
            _settings?.SshTunnelLocalPort ?? 0,
            _settings?.SshTunnelRemotePort ?? 0);
        var tunnel = BuildTunnelInfo();
        var portDiagnostics = PortDiagnosticsService.BuildDiagnostics(topology, tunnel);
        ApplyDetectedSshForwardTopology(topology, portDiagnostics);
        var runtime = BuildGatewayRuntimeInfo(portDiagnostics);
        var warnings = nodes.SelectMany(n => n.Warnings).ToList();
        warnings.AddRange(CommandCenterDiagnostics.BuildTopologyWarnings(topology, tunnel));
        warnings.AddRange(BuildPortDiagnosticWarnings(portDiagnostics, topology, tunnel));
        warnings.AddRange(BuildBrowserProxyAuthWarnings(nodes));

        if (!string.IsNullOrWhiteSpace(_model.AuthFailureMessage))
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "auth",
                Title = "Gateway authentication failed",
                Detail = _model.AuthFailureMessage
            });
        }

        var fullDeviceId = _nodeFullDeviceId();
        if (_nodeIsPendingApproval() && !string.IsNullOrWhiteSpace(fullDeviceId))
        {
            var approvalCommand = $"openclaw devices approve {fullDeviceId}";
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "pairing",
                Title = "Node is waiting for approval",
                Detail = $"Approve device {_nodeShortDeviceId()} from the gateway CLI, then re-open the command center after reconnect.",
                RepairAction = "Copy approval command",
                CopyText = approvalCommand
            });
        }

        if (_model.Status == ConnectionStatus.Error)
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "gateway",
                Title = "Gateway connection error",
                Detail = "The tray is not currently connected to the gateway."
            });
        }
        else if (_model.Status != ConnectionStatus.Connected)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = "Gateway is not connected",
                Detail = $"Current connection state is {_model.Status}."
            });
        }

        var lastCheckTime = _lastCheckTimeProvider();

        if (_model.Status == ConnectionStatus.Connected &&
            DateTime.Now - lastCheckTime > TimeSpan.FromMinutes(2))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = "Gateway health is stale",
                Detail = $"Last health check was {lastCheckTime:t}. Run a health check or verify the localhost tunnel."
            });
        }

        if (_model.Channels.Length == 0 && _model.Status == ConnectionStatus.Connected && _operatorClientProvider() != null)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "channel",
                Title = "No channels reported",
                Detail = "The gateway health payload did not report any channels."
            });
        }
        else if (_model.Channels.Length == 0 && _model.Status == ConnectionStatus.Connected && _settings?.EnableNodeMode == true)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "gateway",
                Title = "Waiting for gateway health",
                Detail = "Node mode is connected. Channel/session inventories are filled from gateway health events when available."
            });
        }
        else if (_model.Channels.Length > 0 && _model.Channels.All(c => !ChannelHealth.IsHealthyStatus(c.Status)))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "channel",
                Title = "No channels are currently running",
                Detail = "Channels are configured but none are reporting a running/ready state."
            });
        }

        if (_model.Status == ConnectionStatus.Connected && nodes.Count == 0 && _operatorClientProvider() != null)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "node",
                Title = "No nodes reported",
                Detail = "node.list did not report any connected nodes. Pair a Windows node or verify the operator token has node inventory access."
            });
        }

        if (_model.UsageCost?.Totals.MissingCostEntries > 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "usage",
                Title = "Some usage costs are missing",
                Detail = $"{_model.UsageCost.Totals.MissingCostEntries} usage entr{(_model.UsageCost.Totals.MissingCostEntries == 1 ? "y is" : "ies are")} missing cost data."
            });
        }

        return new GatewayCommandCenterState
        {
            ConnectionStatus = _model.Status,
            LastRefresh = lastCheckTime.ToUniversalTime(),
            Topology = topology,
            Runtime = runtime,
            Update = _model.UpdateInfo,
            Tunnel = tunnel,
            GatewaySelf = _model.GatewaySelf,
            PortDiagnostics = portDiagnostics,
            Permissions = PermissionDiagnostics.BuildDefaultWindowsMatrix(),
            Channels = _model.Channels.Select(ChannelCommandCenterInfo.FromHealth).ToList(),
            Sessions = _model.Sessions.ToList(),
            Usage = _model.Usage,
            UsageStatus = _model.UsageStatus,
            UsageCost = _model.UsageCost,
            Nodes = nodes,
            Warnings = CommandCenterDiagnostics.SortAndDedupeWarnings(warnings),
            RecentActivity = ActivityStreamService.GetItems(12)
                .Select(item => new CommandCenterActivityInfo
                {
                    Timestamp = item.Timestamp,
                    Category = item.Category,
                    Title = item.Title,
                    Details = item.Details,
                    DashboardPath = item.DashboardPath,
                    SessionKey = item.SessionKey,
                    NodeId = item.NodeId
                })
                .ToList()
        };
    }

    internal IEnumerable<GatewayDiagnosticWarning> BuildBrowserProxyAuthWarnings(IReadOnlyList<NodeCapabilityHealthInfo> nodes)
    {
        if (_settings?.NodeBrowserProxyEnabled == false ||
            !nodes.Any(node => node.BrowserDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase)))
        {
            yield break;
        }

        yield return new GatewayDiagnosticWarning
        {
            Severity = GatewayDiagnosticSeverity.Info,
            Category = "browser",
            Title = "Browser proxy auth may need a gateway token",
            Detail = "This Windows node is advertising browser.proxy without a saved gateway shared token. QR/bootstrap pairing can connect the node, but an authenticated browser-control host may still require the same gateway token in Settings.",
            RepairAction = "Copy browser proxy auth guidance",
            CopyText = "If browser.proxy returns an auth error, enter the gateway shared token in Settings > Gateway Token, or configure the browser-control host to use auth compatible with the Windows node. Do not paste QR bootstrap tokens into the normal gateway token field."
        };
    }

    internal static IEnumerable<GatewayDiagnosticWarning> BuildPortDiagnosticWarnings(
        IReadOnlyList<PortDiagnosticInfo> ports,
        GatewayTopologyInfo topology,
        TunnelCommandCenterInfo? tunnel)
    {
        foreach (var port in ports)
        {
            if (tunnel?.Status == TunnelStatus.Up &&
                port.Purpose.Equals("SSH tunnel local forward", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Warning,
                    Category = "port",
                    Title = "SSH tunnel port is not listening",
                    Detail = port.Detail
                };
            }

            if (topology.DetectedKind == GatewayKind.WindowsNative &&
                port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "port",
                    Title = "No local gateway listener detected",
                    Detail = port.Detail
                };
            }

            if (port.Purpose.Equals("Browser proxy host", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                if (topology.UsesSshTunnel)
                {
                    yield return new GatewayDiagnosticWarning
                    {
                        Severity = GatewayDiagnosticSeverity.Info,
                        Category = "browser",
                        Title = "Browser proxy SSH forward is not listening",
                        Detail = $"browser.proxy over SSH needs a companion local forward for port {port.Port}. Add the browser-control forward to the same tunnel, or enable the managed SSH tunnel so Windows starts both forwards.",
                        RepairAction = "Copy browser proxy SSH forward",
                        CopyText = BuildBrowserProxySshForwardHint(port.Port, tunnel)
                    };
                    continue;
                }

                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "browser",
                    Title = "Browser proxy host not detected",
                    Detail = "browser.proxy needs a compatible browser-control host listening on the gateway port + 2.",
                    RepairAction = "Copy browser setup guidance",
                    CopyText = CommandCenterTextHelper.BuildBrowserSetupGuidance(port.Port, topology, tunnel)
                };
            }
        }
    }

    internal static string BuildBrowserProxySshForwardHint(int browserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (browserProxyPort is < 1 or > 65535)
            return "ssh -N -L <local-browser-port>:127.0.0.1:<remote-browser-port> <user>@<host>";

        var localBrowserPort = ResolveLocalBrowserProxyPort(browserProxyPort, tunnel);
        var target = BuildSshTarget(tunnel);
        var remoteBrowserPort = ResolveRemoteBrowserProxyPort(localBrowserPort, tunnel);
        return remoteBrowserPort is >= 1 and <= 65535
            ? $"ssh -N -L {localBrowserPort}:127.0.0.1:{remoteBrowserPort} {target}"
            : $"ssh -N -L {localBrowserPort}:127.0.0.1:<remote-gateway-port+2> {target}";
    }

    internal static string BuildSshTarget(TunnelCommandCenterInfo? tunnel)
    {
        var host = tunnel?.Host?.Trim();
        var user = tunnel?.User?.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user))
            return $"{user}@{host}";
        if (!string.IsNullOrWhiteSpace(host))
            return $"<user>@{host}";
        return "<user>@<host>";
    }

    internal static int ResolveLocalBrowserProxyPort(int fallbackBrowserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (TryGetEndpointPort(tunnel?.BrowserProxyLocalEndpoint, out var browserLocalPort))
            return browserLocalPort;

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localGatewayPort <= 65533)
        {
            return localGatewayPort + 2;
        }

        return fallbackBrowserProxyPort;
    }

    internal static int? ResolveRemoteBrowserProxyPort(int localBrowserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (TryGetEndpointPort(tunnel?.BrowserProxyRemoteEndpoint, out var browserRemotePort))
            return browserRemotePort;

        if (!TryGetEndpointPort(tunnel?.RemoteEndpoint, out var remoteGatewayPort) ||
            remoteGatewayPort > 65533)
        {
            return null;
        }

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localBrowserProxyPort != localGatewayPort + 2)
        {
            return null;
        }

        return remoteGatewayPort + 2;
    }

    internal static bool TryGetEndpointPort(string? endpoint, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        return separator >= 0 &&
            int.TryParse(endpoint[(separator + 1)..], out port) &&
            port is >= 1 and <= 65535;
    }

    internal static void ApplyDetectedSshForwardTopology(
        GatewayTopologyInfo topology,
        IReadOnlyList<PortDiagnosticInfo> ports)
    {
        if (topology.UsesSshTunnel ||
            topology.DetectedKind != GatewayKind.WindowsNative ||
            !topology.IsLoopback)
        {
            return;
        }

        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null ||
            !gatewayPort.IsListening ||
            !string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        topology.DetectedKind = GatewayKind.MacOverSsh;
        topology.DisplayName = "SSH tunnel (detected)";
        topology.Transport = "ssh tunnel";
        topology.UsesSshTunnel = true;
        topology.Detail = $"Local gateway port {gatewayPort.Port} is owned by ssh, so Command Center treats it as a manually managed SSH local forward.";
    }

    internal static GatewayRuntimeInfo BuildGatewayRuntimeInfo(IReadOnlyList<PortDiagnosticInfo> ports)
    {
        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null || !gatewayPort.IsListening)
            return new GatewayRuntimeInfo();

        return new GatewayRuntimeInfo
        {
            ProcessName = gatewayPort.OwningProcessName ?? "",
            ProcessId = gatewayPort.OwningProcessId,
            Port = gatewayPort.Port,
            IsSshForward = string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase)
        };
    }

    internal TunnelCommandCenterInfo? BuildTunnelInfo()
    {
        if (_settings?.UseSshTunnel != true)
        {
            return null;
        }

        var localPort = _sshTunnelService is { CurrentLocalPort: > 0 }
            ? _sshTunnelService.CurrentLocalPort
            : _settings.SshTunnelLocalPort;
        var remotePort = _sshTunnelService is { CurrentRemotePort: > 0 }
            ? _sshTunnelService.CurrentRemotePort
            : _settings.SshTunnelRemotePort;
        var host = string.IsNullOrWhiteSpace(_sshTunnelService?.CurrentHost)
            ? _settings.SshTunnelHost
            : _sshTunnelService!.CurrentHost!;
        var user = string.IsNullOrWhiteSpace(_sshTunnelService?.CurrentUser)
            ? _settings.SshTunnelUser
            : _sshTunnelService!.CurrentUser!;
        var status = _sshTunnelService?.Status is TunnelStatus.Up or TunnelStatus.Starting or TunnelStatus.Restarting or TunnelStatus.Failed
            ? _sshTunnelService.Status
            : string.IsNullOrWhiteSpace(_sshTunnelService?.LastError)
                ? TunnelStatus.Stopped
                : TunnelStatus.Failed;

        return new TunnelCommandCenterInfo
        {
            Status = status,
            LocalEndpoint = $"127.0.0.1:{localPort}",
            RemoteEndpoint = string.IsNullOrWhiteSpace(host)
                ? $"127.0.0.1:{remotePort}"
                : $"{host}:127.0.0.1:{remotePort}",
            BrowserProxyLocalEndpoint = _sshTunnelService?.CurrentBrowserProxyLocalPort > 0
                ? $"127.0.0.1:{_sshTunnelService.CurrentBrowserProxyLocalPort}"
                : "",
            BrowserProxyRemoteEndpoint = _sshTunnelService?.CurrentBrowserProxyRemotePort > 0
                ? string.IsNullOrWhiteSpace(host)
                    ? $"127.0.0.1:{_sshTunnelService.CurrentBrowserProxyRemotePort}"
                    : $"{host}:127.0.0.1:{_sshTunnelService.CurrentBrowserProxyRemotePort}"
                : "",
            Host = host,
            User = user,
            LastError = _sshTunnelService?.LastError,
            StartedAt = _sshTunnelService?.StartedAtUtc
        };
    }
}
