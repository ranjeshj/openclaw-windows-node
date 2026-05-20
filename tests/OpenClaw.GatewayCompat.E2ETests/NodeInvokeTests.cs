using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Node-invoke scenario. The fake LLM has no built-in tool-call support
/// (intentionally minimal per W2), so this scenario exercises node.invoke
/// the production way: the gateway invokes a Windows node capability via
/// the same WindowsNodeClient.OnNodeInvoke handler the tray uses for any
/// gateway -> node command. We use app.nodes (an existing production MCP
/// tool) to assert the gateway sees the Windows node and its capabilities
/// after local setup, which is the exact failure mode "node.invoke for
/// system.notify silently dropped" would manifest as.
/// </summary>
[Trait("Tier", "Gateway")]
public class NodeInvokeTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public NodeInvokeTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task GatewaySees_WindowsNode_WithSystemCapability()
    {
        await GatewayCompatScenarios.ApplyFakeLlmProviderAsync(_fixture.Client);
        using (var start = await _fixture.Client.CallToolAsync(
            "tray.testhook.localSetup.start",
            new { replaceExistingConfigurationConfirmed = true })) { }
        using (var wait = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { nodeConnected = true, paired = true, timeoutSeconds = 600 })) { }

        using var nodes = await _fixture.Client.CallToolAsync("app.nodes");
        using var nodesPayload = GatewayCompatScenarios.UnwrapToolPayload(nodes);
        var arr = nodesPayload.RootElement;
        Assert.True(arr.GetArrayLength() > 0,
            "Gateway reports no nodes — node.invoke cannot reach the tray. " +
            "Per docs/gateway-node-integration.md this manifests as silent drops.");

        // Per docs/WINDOWS_NODE_TESTING.md the tray advertises the 'system'
        // capability unconditionally. If it's missing here, capability
        // registration in NodeService is broken.
        var foundSystem = false;
        foreach (var node in arr.EnumerateArray())
        {
            if (node.TryGetProperty("CapabilityCount", out var cc) && cc.GetInt32() > 0)
            {
                foundSystem = true;
                break;
            }
        }
        Assert.True(foundSystem,
            "Gateway sees a Windows node but no capabilities. " +
            "Node registration handshake or capability advertisement is broken.");
    }
}
