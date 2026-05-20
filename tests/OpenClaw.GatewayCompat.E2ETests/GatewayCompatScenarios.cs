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
    /// <summary>
    /// Production default WSL distro name (see
    /// <c>LocalGatewaySetupOptions.DistroName</c>). The collection fixture
    /// drives the same production install path the tray uses, so this MUST
    /// match what the engine creates.
    /// </summary>
    public const string DistroName = "OpenClawGateway";

    /// <summary>
    /// Verified against openclaw 2026.5.18. The schema lives at
    /// src/config/zod-schema.core.ts:319 in the gateway repo
    /// (ModelDefinitionSchema, .strict()), and uses BOTH id and name
    /// (both required, min length 1).
    ///
    /// Uses strict JSON (not JSON5) to sidestep parser ambiguity.
    /// </summary>
    public static string FakeLlmProviderPatch(string fakeLlmPort) => $$"""
        {
          "models": {
            "providers": {
              "fake": {
                "api": "openai-completions",
                "baseUrl": "http://127.0.0.1:{{fakeLlmPort}}/v1",
                "apiKey": "test",
                "auth": "api-key",
                "models": [
                  {
                    "id": "fake-llm",
                    "name": "fake-llm",
                    "reasoning": false,
                    "input": ["text"],
                    "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
                    "contextWindow": 200000,
                    "maxTokens": 4096
                  }
                ]
              }
            }
          },
          "agents": {
            "defaults": { "model": { "primary": "fake/fake-llm" } }
          }
        }
        """;

    /// <summary>
    /// Drives the full production local-setup flow against an already-running
    /// E2E tray: kicks off <c>tray.testhook.localSetup.start</c>, polls
    /// <c>tray.testhook.localSetup.status</c> until it reaches a terminal
    /// state, bootstraps fake-LLM inside the production WSL distro, then
    /// applies the fake-LLM provider patch. Shared by
    /// <see cref="GatewayCollectionFixture"/> and per-class fixtures (e.g.
    /// reconnect) that need the same end state.
    /// </summary>
    public static async Task DriveLocalSetupAndPrepareGatewayAsync(
        McpClient client,
        TimeSpan? localSetupTimeout = null)
    {
        var timeout = localSetupTimeout ?? TimeSpan.FromMinutes(20);

        using (var startResp = await client.CallToolAsync(
            "tray.testhook.localSetup.start",
            new { replaceExistingConfigurationConfirmed = true }).ConfigureAwait(false))
        {
            using var startPayload = UnwrapToolPayload(startResp);
            if (!startPayload.RootElement.TryGetProperty("started", out var started) || !started.GetBoolean())
            {
                throw new InvalidOperationException(
                    "tray.testhook.localSetup.start did not report started=true. Payload: " +
                    startPayload.RootElement.GetRawText());
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        string? lastStatus = null;
        string? lastMessage = null;
        while (DateTime.UtcNow < deadline)
        {
            using var statusResp = await client.CallToolAsync(
                "tray.testhook.localSetup.status").ConfigureAwait(false);
            using var statusPayload = UnwrapToolPayload(statusResp);
            var root = statusPayload.RootElement;

            if (root.TryGetProperty("status", out var s)) lastStatus = s.GetString();
            if (root.TryGetProperty("message", out var m)) lastMessage = m.GetString();

            if (root.TryGetProperty("isTerminal", out var term) && term.GetBoolean())
            {
                if (!string.Equals(lastStatus, "Complete", StringComparison.OrdinalIgnoreCase))
                {
                    var err = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;
                    throw new InvalidOperationException(
                        "localSetup did not Complete. status=" + lastStatus +
                        ", message=" + lastMessage + ", error=" + err);
                }
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException(
                $"localSetup did not reach a terminal state within {timeout}. " +
                "Last status=" + lastStatus + ", message=" + lastMessage);
        }

        await StartFakeLlmInDistroAsync().ConfigureAwait(false);
        await ApplyFakeLlmProviderAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Shells out to <c>wsl.exe</c> to bootstrap fake-LLM inside the
    /// production WSL distro created by local setup. Acceptable harness
    /// scaffolding — not a <c>tray.testhook.*</c> tool because it sets up
    /// the LLM mock, which is not a production behavior.
    /// </summary>
    public static async Task StartFakeLlmInDistroAsync()
    {
        var repoRoot = FindRepoRoot();
        var repoWsl = ToWslPath(repoRoot);
        var scriptWsl = repoWsl + "/tools/spike/start-fake-llm.sh";
        var port = FakeLlmPort;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // wsl.exe emits UTF-16 LE; force UTF-8 for capture.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(DistroName);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("openclaw");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(
            $"REPO_WSL_PATH='{repoWsl}' FAKE_LLM_PORT='{port}' bash '{scriptWsl}'");

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wsl.exe to bootstrap fake-LLM");
        var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "fake-LLM bring-up via wsl.exe failed (exit " + p.ExitCode + ").\n" +
                "stdout:\n" + stdout + "\nstderr:\n" + stderr);
        }
    }

    public static string FakeLlmPort =>
        Environment.GetEnvironmentVariable("FAKE_LLM_PORT") ?? "18888";

    /// <summary>
    /// Climbs from <see cref="AppContext.BaseDirectory"/> looking for
    /// <c>openclaw-windows-node.slnx</c>. Mirrors the private helper in
    /// <see cref="GatewayCompatFixture"/> so the collection fixture (and
    /// any other harness scaffolding outside the fixture) can resolve
    /// repo-relative resources (spike scripts, fake-LLM server.mjs).
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "openclaw-windows-node.slnx")))
                return dir;
            var parent = System.IO.Directory.GetParent(dir)?.FullName;
            if (parent == dir || parent is null) break;
            dir = parent;
        }
        throw new System.IO.DirectoryNotFoundException(
            "Could not locate repo root (openclaw-windows-node.slnx) from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Maps a Windows path like <c>C:\repos\foo</c> to its WSL mount path
    /// <c>/mnt/c/repos/foo</c>. Used to hand repo-relative paths to bash
    /// commands launched via <c>wsl.exe</c>.
    /// </summary>
    public static string ToWslPath(string windowsPath)
    {
        var p = windowsPath.Replace('\\', '/').TrimEnd('/');
        if (p.Length >= 2 && p[1] == ':')
        {
            return "/mnt/" + char.ToLowerInvariant(p[0]) + p.Substring(2);
        }
        return p;
    }

    /// <summary>
    /// Client-side polling around short server-side waits. McpClient has a
    /// 30s HTTP timeout, so we slice long waits into &lt;=20s server-side
    /// calls and re-poll until the target is reached or the deadline expires.
    /// </summary>
    public static async Task<JsonDocument> WaitForConnectionAsync(
        McpClient client,
        object waitArgsBase,
        TimeSpan timeout,
        TimeSpan serverSlice = default)
    {
        if (serverSlice == default) serverSlice = TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + timeout;
        JsonDocument? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < serverSlice ? remaining : serverSlice;
            // Re-emit args with our slice as timeoutSeconds. We accept any
            // anonymous object that already specifies the connection flags
            // (operatorConnected, nodeConnected, paired, targetOverallState).
            var args = MergeTimeout(waitArgsBase, (int)Math.Max(1, slice.TotalSeconds));
            using var resp = await client.CallToolAsync("tray.testhook.connection.waitFor", args).ConfigureAwait(false);
            var payload = UnwrapToolPayload(resp);
            if (payload.RootElement.TryGetProperty("reached", out var reached) && reached.GetBoolean())
            {
                last?.Dispose();
                return payload;
            }
            last?.Dispose();
            last = payload;
        }
        return last ?? throw new TimeoutException("connection.waitFor never returned a payload");
    }

    private static object MergeTimeout(object args, int timeoutSeconds)
    {
        var json = JsonSerializer.SerializeToElement(args);
        using var doc = JsonDocument.Parse(json.GetRawText());
        var dict = new System.Collections.Generic.Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "timeoutSeconds", StringComparison.OrdinalIgnoreCase)) continue;
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                _ => JsonSerializer.Deserialize<object>(prop.Value.GetRawText()),
            };
        }
        dict["timeoutSeconds"] = timeoutSeconds;
        return dict;
    }

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
