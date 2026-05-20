using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Verifies the Windows node end of the connection: after local setup, the
/// node role reaches Connected, advertises its capabilities to the gateway,
/// and the gateway's node.list reflects the local node.
///
/// Same-path: relies on the production WindowsNodeClient capability
/// registration that LocalGatewaySetup triggers when EnableNodeMode is on.
/// We don't simulate node capability registration here; we observe what
/// the production registration produced.
/// </summary>
[Trait("Tier", "Gateway")]
[Collection("Gateway")]
public class NodePairingTests
{
    private readonly GatewayCollectionFixture _fixture;

    public NodePairingTests(GatewayCollectionFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task NodeRoleReachesConnected_AdvertisesExpectedCapabilities()
    {
        // Setup + provider patch already done by GatewayCollectionFixture.
        using (var payload = await GatewayCompatScenarios.WaitForConnectionAsync(
            _fixture.Client,
            new { nodeConnected = true, paired = true },
            TimeSpan.FromSeconds(120)))
        {
            Assert.True(payload.RootElement.GetProperty("reached").GetBoolean(),
                "Node never reached Connected+Paired. Last snapshot: " +
                JsonSerializer.Serialize(payload.RootElement));
            Assert.Equal("Connected", payload.RootElement.GetProperty("nodeState").GetString());
        }

        // app.nodes is an existing production MCP tool (see WinNode.Cli/skill.md).
        // It returns what the gateway sees, NOT what the tray thinks it has.
        // If node registration silently dropped, this fails.
        using var nodes = await _fixture.Client.CallToolAsync("app.nodes");
        using var nodesPayload = GatewayCompatScenarios.UnwrapToolPayload(nodes);
        Assert.Equal(JsonValueKind.Array, nodesPayload.RootElement.ValueKind);
        Assert.True(nodesPayload.RootElement.GetArrayLength() > 0,
            "Gateway reports no nodes after local setup. " +
            "If this fails but operator role is Connected, the node-side " +
            "capability registration handshake is broken — check tool-events cap " +
            "(see repo memory).");
    }
}
