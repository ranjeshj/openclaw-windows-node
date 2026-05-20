using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// After Ensure-TestGateway.ps1 stood up the gateway and GatewayCompatFixture
/// pre-seeded the bootstrap token, the tray auto-pairs the operator role on
/// startup. This test waits for the operator side to reach Connected and
/// asserts the resulting state is observable via diagnostics.dump (so the
/// state is durable, not a transient handshake side-effect).
/// </summary>
[Trait("Tier", "Gateway")]
public class OperatorPairingTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public OperatorPairingTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task OperatorRoleReachesConnected_AndPersistsDeviceToken()
    {
        using var waitResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { operatorConnected = true, timeoutSeconds = 120 });
        using var waitPayload = GatewayCompatScenarios.UnwrapToolPayload(waitResp);
        var root = waitPayload.RootElement;
        Assert.True(root.GetProperty("reached").GetBoolean(),
            "Operator role never reached Connected. Last snapshot: " +
            JsonSerializer.Serialize(root));
        Assert.Equal("Connected", root.GetProperty("operatorState").GetString());
        Assert.NotNull(root.GetProperty("operatorDeviceId").GetString());

        using var diagResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.diagnostics.dump");
        using var diagPayload = GatewayCompatScenarios.UnwrapToolPayload(diagResp);
        Assert.True(diagPayload.RootElement.GetProperty("node").TryGetProperty("attached", out _));
    }
}
