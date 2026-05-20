using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// End-to-end chat round-trip: tray.testhook.chat.send -> gateway ->
/// fake-LLM -> back. Confirms the OpenAI-compatible provider wiring works,
/// the chat path is reachable, and the fake LLM saw the expected message.
///
/// Same-path: chat.send forwards to OpenClawChatDataProvider.SendMessageAsync
/// (same method ChatWindow.OnSendClicked invokes). We assert what the fake
/// LLM actually received rather than what the tray thinks it sent — that
/// catches every layer between the click handler and the wire.
/// </summary>
[Trait("Tier", "Gateway")]
public class ChatRoundTripTests : IClassFixture<GatewayCompatFixture>
{
    private readonly GatewayCompatFixture _fixture;

    public ChatRoundTripTests(GatewayCompatFixture fixture) => _fixture = fixture;

    [GatewayCompatFact]
    public async Task ChatSend_ReachesFakeLlm_WithExpectedMessage()
    {
        await GatewayCompatScenarios.ApplyFakeLlmProviderAsync(_fixture.Client);
        using (var start = await _fixture.Client.CallToolAsync(
            "tray.testhook.localSetup.start",
            new { replaceExistingConfigurationConfirmed = true })) { }
        using (var wait = await _fixture.Client.CallToolAsync(
            "tray.testhook.connection.waitFor",
            new { targetOverallState = "Ready", timeoutSeconds = 600 })) { }

        // Reset the fake-LLM's request log so we can assert exactly what it sees.
        var fakeLlmPort = GatewayCompatScenarios.FakeLlmPort;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            await http.PostAsync($"http://127.0.0.1:{fakeLlmPort}/__assert/reset",
                new StringContent("")).ConfigureAwait(false);
        }

        const string expectedMessage = "round-trip ping from chat scenario";
        using var send = await _fixture.Client.CallToolAsync(
            "tray.testhook.chat.send",
            new { threadId = "main", message = expectedMessage, timeoutSeconds = 90 });
        using var sendPayload = GatewayCompatScenarios.UnwrapToolPayload(send);
        Assert.True(sendPayload.RootElement.GetProperty("sent").GetBoolean(),
            "chat.send returned sent=false: " + JsonSerializer.Serialize(sendPayload.RootElement));

        // Assert the fake LLM actually received the user message.
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            var raw = await http.GetStringAsync($"http://127.0.0.1:{fakeLlmPort}/__assert/last-request");
            using var doc = JsonDocument.Parse(raw);
            var count = doc.RootElement.GetProperty("requestCount").GetInt32();
            Assert.True(count >= 1,
                "Fake LLM saw no requests. chat.send returned sent=true but the " +
                "gateway never forwarded to the provider. requestCount=" + count);
            // Walk last-request -> body -> messages -> [N] -> content for the user message.
            var last = doc.RootElement.GetProperty("lastRequest").GetProperty("body");
            var messages = last.GetProperty("messages");
            var sawExpected = false;
            foreach (var m in messages.EnumerateArray())
            {
                if (m.TryGetProperty("content", out var content))
                {
                    var text = content.ValueKind == JsonValueKind.String
                        ? content.GetString()
                        : content.GetRawText();
                    if (text is not null && text.Contains(expectedMessage, StringComparison.Ordinal))
                    {
                        sawExpected = true;
                        break;
                    }
                }
            }
            Assert.True(sawExpected,
                "Fake LLM received a request but the user message was not present. " +
                "Body: " + last.GetRawText());
        }
    }
}
