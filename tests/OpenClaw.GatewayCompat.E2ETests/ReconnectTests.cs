using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Reconnect resilience: after pairing, reset the operator/node pairing
/// state via tray.testhook.pairing.reset and verify the tray reconnects
/// cleanly (re-pairs) rather than silently degrading.
///
/// Same-path: pairing.reset invokes GatewayRegistry.Remove + per-gateway
/// identity wipe (same operations the Settings page uses when the user
/// clicks "Reset pairing"). The reconnect that follows is the same code
/// path the tray runs when it boots fresh.
/// </summary>
[Trait("Tier", "Gateway")]
public class ReconnectTests : IClassFixture<ReconnectFixture>
{
    private readonly ReconnectFixture _fixture;

    public ReconnectTests(ReconnectFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task PairingReset_FollowedByReSetup_ReachesReadyAgain()
    {
        // Phase 1: initial setup is already done by ReconnectFixture.
        //          Just confirm the connection has settled at Ready.
        using (var p = await GatewayCompatScenarios.WaitForConnectionAsync(
            _fixture.Client,
            new { targetOverallState = "Ready" },
            TimeSpan.FromSeconds(120)))
        {
            Assert.True(p.RootElement.GetProperty("reached").GetBoolean(),
                "Phase 1 (initial setup) did not reach Ready: " +
                JsonSerializer.Serialize(p.RootElement));
        }

        // Phase 2: reset pairing.
        using (var reset = await _fixture.Client.CallToolAsync(
            "tray.testhook.pairing.reset",
            new { wipeIdentityFiles = true }))
        {
            using var p = GatewayCompatScenarios.UnwrapToolPayload(reset);
            // Reset should drop at least one gateway record. If it doesn't,
            // the first-time setup never persisted credentials — a deeper
            // bug we'd want to know about.
            Assert.True(p.RootElement.GetProperty("removedGatewayIds").GetArrayLength() > 0,
                "pairing.reset removed nothing. Either initial setup didn't " +
                "persist a gateway record, or pairing.reset is wired wrong.");
        }

        // Phase 3: re-pair using the same setup flow (with replace=true
        // because the legacy state may still be on disk). Use the shared
        // helper so the re-pair drives the same end state.
        await GatewayCompatScenarios.DriveLocalSetupAndPrepareGatewayAsync(
            _fixture.Client, TimeSpan.FromMinutes(20)).ConfigureAwait(false);

        using (var p = await GatewayCompatScenarios.WaitForConnectionAsync(
            _fixture.Client,
            new { targetOverallState = "Ready" },
            TimeSpan.FromSeconds(120)))
        {
            Assert.True(p.RootElement.GetProperty("reached").GetBoolean(),
                "Phase 3 (re-pair after reset) did not reach Ready: " +
                JsonSerializer.Serialize(p.RootElement));
            Assert.NotNull(p.RootElement.GetProperty("operatorDeviceId").GetString());
        }
    }
}
