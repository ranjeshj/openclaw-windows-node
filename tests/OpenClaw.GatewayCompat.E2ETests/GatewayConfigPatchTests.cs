using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Verifies that the harness can inject the fake-LLM provider into the
/// openclaw gateway config via <c>tray.testhook.gateway.config.patch</c>,
/// and that <c>openclaw config validate</c> accepts the resulting config.
///
/// This is the foundation scenario every chat/node.invoke test depends on:
/// if the gateway rejects the fake-provider config, nothing downstream
/// works. Catches schema drift in the openclaw config root.
/// </summary>
[Trait("Tier", "Gateway")]
public class GatewayConfigPatchTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public GatewayConfigPatchTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task FakeLlmProvider_PatchAndValidateSucceed()
    {
        // The patch shape was verified against openclaw 2026.5.18 by the W0
        // spike (see tools/fake-llm-server/README.md). If a future gateway
        // version moves the keys, this assertion fails loudly and the
        // gateway-lkg-bump auto-PR is blocked - which is the whole point.
        var port = Environment.GetEnvironmentVariable("FAKE_LLM_PORT") ?? "18888";
        var patch = $$"""
        {
          models: {
            providers: {
              fake: {
                api: "openai-completions",
                baseUrl: "http://127.0.0.1:{{port}}/v1",
                apiKey: "test",
                authMode: "api-key",
                models: [ { id: "fake-llm" } ]
              }
            }
          },
          agents: {
            defaults: { model: { primary: "fake/fake-llm" } }
          }
        }
        """;

        using var response = await _fixture.Client.CallToolAsync(
            "tray.testhook.gateway.config.patch",
            new
            {
                distroName = "Ubuntu-24.04",
                patchJson = patch,
            });

        // The tool returns the NodeInvokeResponse payload as the MCP tool
        // result text body; parse it and assert each phase succeeded.
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
            "accepted by this gateway version. Update tools/fake-llm-server/README.md " +
            "after consulting `openclaw config schema`. Stderr: " +
            root.GetProperty("validateStderr").GetString());
    }
}
