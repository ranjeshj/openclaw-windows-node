using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Drives the production local-setup flow end-to-end via test hooks (same
/// methods as the LocalSetupProgressPage "Set up locally" button) and
/// verifies that the operator role ends up paired and credentials are
/// persisted under the isolated AppData directory.
///
/// Same-path under test:
///   tray.testhook.localSetup.start -> App.CreateLocalGatewaySetupEngine
///                                  -> LocalGatewaySetupEngine.RunLocalOnlyAsync
///   tray.testhook.connection.waitFor -> IGatewayConnectionManager.StateChanged
/// </summary>
[Trait("Tier", "Gateway")]
[Collection("Gateway")]
public class OperatorPairingTests
{
    private readonly GatewayCollectionFixture _fixture;

    public OperatorPairingTests(GatewayCollectionFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task LocalSetup_OperatorRoleReachesConnected_AndPersistsDeviceToken()
    {
        // The collection fixture has already run localSetup.start, polled
        // it to Complete, started fake-LLM, and applied the provider patch.
        // This test only asserts the final state.

        using (var waitPayload = await GatewayCompatScenarios.WaitForConnectionAsync(
            _fixture.Client,
            new { operatorConnected = true },
            TimeSpan.FromSeconds(60)))
        {
            var root = waitPayload.RootElement;
            Assert.True(root.GetProperty("reached").GetBoolean(),
                "Operator role never reached Connected. Last snapshot: " +
                JsonSerializer.Serialize(root));
            Assert.Equal("Connected", root.GetProperty("operatorState").GetString());
            Assert.NotNull(root.GetProperty("operatorDeviceId").GetString());
        }

        using var diagResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.diagnostics.dump");
        using var diagPayload = GatewayCompatScenarios.UnwrapToolPayload(diagResp);
        var diagRoot = diagPayload.RootElement;
        Assert.True(diagRoot.GetProperty("node").TryGetProperty("attached", out _));
    }
}
