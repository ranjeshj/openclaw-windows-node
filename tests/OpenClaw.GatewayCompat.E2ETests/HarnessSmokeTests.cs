using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Smoke tests for the gateway-compat harness itself — no WSL, no real
/// gateway. Confirms the fixture can spawn an E2E-built tray and that the
/// <c>tray.testhook.diagnostics.dump</c> tool round-trips. If this test
/// goes red, the rest of the gateway-compat lane is meaningless.
/// </summary>
[Trait("Tier", "Smoke")]
public class HarnessSmokeTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public HarnessSmokeTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ToolsList_ContainsTrayTestHookSurface()
    {
        using var response = await _fixture.Client.ListToolsAsync();
        var json = response.RootElement.GetRawText();
        Assert.Contains("tray.testhook.diagnostics.dump", json);
        Assert.Contains("tray.testhook.localSetup.start", json);
        Assert.Contains("tray.testhook.gateway.config.patch", json);
    }

    [Fact]
    public async Task DiagnosticsDump_ReturnsExpectedShape()
    {
        using var response = await _fixture.Client.CallToolAsync(
            "tray.testhook.diagnostics.dump");

        // MCP tools/call wraps results as
        //   result: { content: [{ type: "text", text: "<json string>" }] }
        // The NodeCapability payload is serialized as the text body. Parse
        // it back out and assert the shape.
        var result = response.RootElement.GetProperty("result");
        var content = result.GetProperty("content")[0];
        var payloadText = content.GetProperty("text").GetString()
            ?? throw new InvalidOperationException("tool result text was null");
        using var payload = JsonDocument.Parse(payloadText);

        // The capability sends Ok+Payload through node-invoke shape; the
        // bridge unwraps to the inner payload object for MCP.
        var diag = payload.RootElement;
        Assert.Equal(1, diag.GetProperty("schemaVersion").GetInt32());
        Assert.True(diag.TryGetProperty("gatewayLkgVersion", out var lkg));
        Assert.False(string.IsNullOrEmpty(lkg.GetString()));
        Assert.True(diag.TryGetProperty("processId", out _));
        Assert.True(diag.TryGetProperty("trayUptimeSeconds", out _));
        Assert.True(diag.TryGetProperty("node", out _));
    }
}
