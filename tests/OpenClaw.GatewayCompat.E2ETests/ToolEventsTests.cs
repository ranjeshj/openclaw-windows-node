using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Regression guard for the "tool-events cap missing" bug. We send a chat
/// message and assert chat.send reports sent=true (the cap registration
/// happens during the production connect handshake on tray startup).
/// </summary>
[Trait("Tier", "Gateway")]
public class ToolEventsTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public ToolEventsTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task GatewayBroadcasts_ToolStreamEvents_AfterChatSend()
    {
        using (var wait = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { targetOverallState = "Ready", timeoutSeconds = 120 })) { }

        using var send = await _fixture.Client.CallToolAsync(
            "tray.testhook.chat.send",
            new { threadId = "main", message = "Hello from tool-events test", timeoutSeconds = 60 });
        using var sendPayload = GatewayCompatScenarios.UnwrapToolPayload(send);
        Assert.True(sendPayload.RootElement.GetProperty("sent").GetBoolean(),
            "chat.send returned sent=false. Payload: " +
            JsonSerializer.Serialize(sendPayload.RootElement));

        using var diag = await _fixture.Client.CallToolAsync(
            "tray.testhook.diagnostics.dump");
        using var diagPayload = GatewayCompatScenarios.UnwrapToolPayload(diag);
        Assert.True(diagPayload.RootElement.GetProperty("trayUptimeSeconds").GetDouble() > 0);
    }
}
