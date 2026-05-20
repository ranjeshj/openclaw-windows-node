using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Verifies that the harness can inject the fake-LLM provider into the
/// openclaw gateway config via <c>tray.testhook.gateway.config.patch</c>
/// and that <c>openclaw config validate</c> accepts the resulting config.
///
/// Under Plan A the gateway is installed by <see cref="GatewayCollectionFixture"/>
/// (production path), and the fixture has already applied the verified
/// patch from <see cref="GatewayCompatScenarios.FakeLlmProviderPatch(string)"/>
/// on startup. This test re-applies the canonical patch and asserts each
/// phase succeeded — exercising patch idempotence AND catching schema
/// drift against the actual installed gateway.
/// </summary>
[Trait("Tier", "Gateway")]
[Collection("Gateway")]
public class GatewayConfigPatchTests
{
    private readonly GatewayCollectionFixture _fixture;

    public GatewayConfigPatchTests(GatewayCollectionFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task FakeLlmProvider_PatchAndValidateSucceed()
    {
        // Verified patch shape (against openclaw 2026.5.18). Strict JSON,
        // both id and name on each model entry, full cost/window/maxTokens.
        // If a future gateway version moves the keys, this assertion fails
        // loudly and the gateway-lkg-bump auto-PR is blocked - which is the
        // whole point.
        using var response = await _fixture.Client.CallToolAsync(
            "tray.testhook.gateway.config.patch",
            new
            {
                distroName = GatewayCompatScenarios.DistroName,
                patchJson = GatewayCompatScenarios.FakeLlmProviderPatch(
                    GatewayCompatScenarios.FakeLlmPort),
            });

        var text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?? throw new InvalidOperationException("tool result text was null");
        using var payload = JsonDocument.Parse(text);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("writeOk").GetBoolean(),
            "Patch write to WSL filesystem failed: " +
            root.GetProperty("writeStderr").GetString());
        Assert.True(root.GetProperty("patchOk").GetBoolean(),
            "openclaw config patch failed: " +
            root.GetProperty("patchStderr").GetString());
        Assert.True(root.GetProperty("validateOk").GetBoolean(),
            "openclaw config validate failed - the patch shape is no longer " +
            "accepted by this gateway version. Update " +
            "GatewayCompatScenarios.FakeLlmProviderPatch after consulting " +
            "`openclaw config schema`. Stderr: " +
            root.GetProperty("validateStderr").GetString());
    }
}
