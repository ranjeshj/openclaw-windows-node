using System;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Tray.IntegrationTests; // McpClient

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Helpers shared by gateway-tier scenario tests. Centralizes the verified
/// fake-LLM provider patch (so a schema bump fixes all scenarios in one
/// place), the WSL distro name the spike workflow uses, and tiny MCP
/// result-unwrapping helpers.
/// </summary>
internal static class GatewayCompatScenarios
{
    public const string DistroName = "Ubuntu-24.04";

    /// <summary>
    /// Verified against openclaw 2026.5.18 by the W0 spike, then refined
    /// via PR-triggered runs (26142893903 and predecessors) which surfaced
    /// the real required shape per <c>openclaw config validate</c>:
    /// models.providers.&lt;id&gt;.models[] requires BOTH id and name plus
    /// reasoning/input/cost/contextWindow/maxTokens. The full shape comes
    /// from openclaw's own internal test fixture
    /// (src/config/model-alias-defaults.test.ts in the gateway repo).
    /// </summary>
    public static string FakeLlmProviderPatch(string fakeLlmPort) => $$"""
        {
          models: {
            providers: {
              fake: {
                api: "openai-completions",
                baseUrl: "http://127.0.0.1:{{fakeLlmPort}}/v1",
                apiKey: "test",
                auth: "api-key",
                models: [
                  {
                    id: "fake-llm",
                    name: "fake-llm",
                    reasoning: false,
                    input: ["text"],
                    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
                    contextWindow: 200000,
                    maxTokens: 4096
                  }
                ]
              }
            }
          },
          agents: {
            defaults: { model: { primary: "fake/fake-llm" } }
          }
        }
        """;

    public static string FakeLlmPort =>
        Environment.GetEnvironmentVariable("FAKE_LLM_PORT") ?? "18888";

    /// <summary>
    /// Run gateway.config.patch with the verified fake-LLM provider patch
    /// and assert all three phases (write, patch, validate) succeeded.
    /// Most scenarios should call this once during their setup.
    /// </summary>
    public static async Task ApplyFakeLlmProviderAsync(McpClient client)
    {
        using var resp = await client.CallToolAsync(
            "tray.testhook.gateway.config.patch",
            new
            {
                distroName = DistroName,
                patchJson = FakeLlmProviderPatch(FakeLlmPort),
            });
        var payload = UnwrapToolPayload(resp);
        var root = payload.RootElement;
        if (!(root.GetProperty("writeOk").GetBoolean()
              && root.GetProperty("patchOk").GetBoolean()
              && root.GetProperty("validateOk").GetBoolean()))
        {
            throw new InvalidOperationException(
                "Could not apply fake-LLM provider patch. " +
                "writeOk=" + root.GetProperty("writeOk").GetBoolean() +
                ", patchOk=" + root.GetProperty("patchOk").GetBoolean() +
                ", validateOk=" + root.GetProperty("validateOk").GetBoolean() +
                ", validateStderr=" + root.GetProperty("validateStderr").GetString());
        }
        payload.Dispose();
    }

    /// <summary>
    /// MCP tools/call wraps NodeInvokeResponse.Payload as the text body of
    /// the first content element. Parse it back to JSON for assertions.
    /// </summary>
    public static JsonDocument UnwrapToolPayload(JsonDocument mcpResponse)
    {
        var text = mcpResponse.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?? throw new InvalidOperationException("MCP tool returned null text content");
        return JsonDocument.Parse(text);
    }
}
