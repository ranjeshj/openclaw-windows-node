using OpenClaw.Shared;
using System;

namespace OpenClawTray.Services;

/// <summary>
/// Adapts a lazily-resolved <c>NodeService</c> reference to <see cref="INodeRuntimeInfo"/>.
/// The provider delegate is invoked on each property access, so the adapter always
/// reflects the current <c>NodeService</c> state even if the service is created after
/// the adapter is constructed.
/// </summary>
internal sealed class NodeRuntimeInfoAdapter : INodeRuntimeInfo
{
    private readonly Func<NodeService?> _nodeServiceProvider;

    public NodeRuntimeInfoAdapter(Func<NodeService?> nodeServiceProvider)
    {
        _nodeServiceProvider = nodeServiceProvider;
    }

    public GatewayNodeInfo? LocalNode => _nodeServiceProvider()?.GetLocalNodeInfo();
    public bool IsPendingApproval => _nodeServiceProvider()?.IsPendingApproval == true;
    public string? FullDeviceId => _nodeServiceProvider()?.FullDeviceId;
    public string? ShortDeviceId => _nodeServiceProvider()?.ShortDeviceId;
}
