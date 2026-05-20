using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// After Ensure-TestGateway.ps1 + GatewayCompatFixture bootstrap-pair, the
/// Windows node side should auto-connect, advertise its capabilities, and
/// be visible via app.nodes.
/// </summary>
[Trait("Tier", "Gateway")]
public class NodePairingTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public NodePairingTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task NodeRoleReachesConnected_AdvertisesExpectedCapabilities()
    {
        using (var wait = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { nodeConnected = true, paired = true, timeoutSeconds = 120 }))
        {
            using var payload = GatewayCompatScenarios.UnwrapToolPayload(wait);
            Assert.True(payload.RootElement.GetProperty("reached").GetBoolean(),
                "Node never reached Connected+Paired. Last snapshot: " +
                JsonSerializer.Serialize(payload.RootElement));
            Assert.Equal("Connected", payload.RootElement.GetProperty("nodeState").GetString());
        }

        using var nodes = await _fixture.Client.CallToolAsync("app.nodes");
        using var nodesPayload = GatewayCompatScenarios.UnwrapToolPayload(nodes);
        Assert.Equal(JsonValueKind.Array, nodesPayload.RootElement.ValueKind);
        Assert.True(nodesPayload.RootElement.GetArrayLength() > 0,
            "Gateway reports no nodes after pairing. If this fails but operator " +
            "role is Connected, the node-side capability registration handshake " +
            "is broken - check tool-events cap.");
    }
}
