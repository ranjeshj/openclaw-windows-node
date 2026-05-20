using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Re-applies the fake-LLM provider patch against the gateway already
/// provisioned by Ensure-TestGateway.ps1. Asserts the gateway accepts
/// the patch shape (write + patch + validate). Catches schema drift.
/// </summary>
[Trait("Tier", "Gateway")]
public class GatewayConfigPatchTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public GatewayConfigPatchTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task FakeLlmProvider_PatchAndValidateSucceed()
    {
        // The patch shape was verified against openclaw 2026.5.18.
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
            "accepted by this gateway version. Stderr: " +
            root.GetProperty("validateStderr").GetString());
    }
}
