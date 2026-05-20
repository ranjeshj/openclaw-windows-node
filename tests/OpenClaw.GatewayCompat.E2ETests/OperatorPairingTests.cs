using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// W4 placeholder. Real scenario lands once
/// <c>tray.testhook.localSetup.start</c> / <c>gateway.config.patch</c>
/// stop returning "not yet implemented". The placeholder exists now so
/// the gateway-compat CI workflow has a real test target to depend on.
/// </summary>
[Trait("Tier", "Gateway")]
public class OperatorPairingTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public OperatorPairingTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task OperatorPairing_BootstrapToDeviceToken_PersistsAndPrecedenceHolds()
    {
        // Pending W3.2 follow-up: tray.testhook.localSetup.* + gateway.config.patch.
        // Expected flow once those exist:
        //   1. tray.testhook.gateway.config.patch — inject fake-LLM provider
        //   2. tray.testhook.localSetup.start — drive LocalGatewaySetupEngine
        //   3. tray.testhook.connection.waitFor — block until paired
        //   4. tray.testhook.diagnostics.dump — assert device token persisted
        //   5. Assert gateways.json under isolated APPDATA has the device token
        //      and that bootstrap is no longer in active credential precedence
        //      (see docs/CONNECTION_ARCHITECTURE.md).
        await Task.CompletedTask;
        Assert.Fail("Implementation pending — W3.2 follow-up tools required.");
    }
}
