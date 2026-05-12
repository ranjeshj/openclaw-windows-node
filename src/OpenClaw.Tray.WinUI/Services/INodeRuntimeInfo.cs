using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Read-only view of node service runtime state needed by <see cref="CommandCenterBuilder"/>
/// and <see cref="DiagnosticsClipboardService"/>. Decouples consumers from the full
/// <c>NodeService</c> and its many unrelated members.
/// </summary>
internal interface INodeRuntimeInfo
{
    GatewayNodeInfo? LocalNode { get; }
    bool IsPendingApproval { get; }
    string? FullDeviceId { get; }
    string? ShortDeviceId { get; }
}
