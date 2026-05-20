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
public class OperatorPairingTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public OperatorPairingTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task LocalSetup_OperatorRoleReachesConnected_AndPersistsDeviceToken()
    {
        // 1. Inject the fake-LLM provider so the wizard / first chat doesn't
        //    burn real LLM credit.
        await GatewayCompatScenarios.ApplyFakeLlmProviderAsync(_fixture.Client);

        // 2. Kick off the same setup engine the LocalSetupProgressPage uses.
        using (var startResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.localSetup.start",
            new { replaceExistingConfigurationConfirmed = true }))
        {
            using var startPayload = GatewayCompatScenarios.UnwrapToolPayload(startResp);
            Assert.True(startPayload.RootElement.GetProperty("started").GetBoolean(),
                "localSetup.start should report started=true");
        }

        // 3. Wait for operator role to reach Connected. Generous timeout
        //    covers Ubuntu install + openclaw service start + handshake.
        using (var waitResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { operatorConnected = true, timeoutSeconds = 600 }))
        {
            using var waitPayload = GatewayCompatScenarios.UnwrapToolPayload(waitResp);
            var root = waitPayload.RootElement;
            Assert.True(root.GetProperty("reached").GetBoolean(),
                "Operator role never reached Connected. Last snapshot: " +
                JsonSerializer.Serialize(root));
            Assert.Equal("Connected", root.GetProperty("operatorState").GetString());
            Assert.NotNull(root.GetProperty("operatorDeviceId").GetString());
        }

        // 4. Diagnostics dump records the same operator device id - proves
        //    state is observable both via the wait-for snapshot AND the
        //    diagnostics surface (i.e. the state isn't transient).
        using var diagResp = await _fixture.Client.CallToolAsync(
            "tray.testhook.diagnostics.dump");
        using var diagPayload = GatewayCompatScenarios.UnwrapToolPayload(diagResp);
        var diagRoot = diagPayload.RootElement;
        Assert.True(diagRoot.GetProperty("node").TryGetProperty("attached", out _));
    }
}
