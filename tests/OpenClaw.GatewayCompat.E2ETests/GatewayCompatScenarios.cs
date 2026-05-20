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
    ///
    /// <para>Retries <see cref="LocalSetupStatus"/> = <c>FailedRetryable</c>
    /// once: the production UI surfaces a "Retry" button on retryable
    /// failures (e.g. transient WSL install hiccups on fresh runners), so
    /// the harness exercises the same UX path.</para>
    /// </summary>
    public static async Task DriveLocalSetupAndPrepareGatewayAsync(
        McpClient client,
        TimeSpan? localSetupTimeout = null,
        int maxRetries = 1)
    {
        var timeout = localSetupTimeout ?? TimeSpan.FromMinutes(25);
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await RunLocalSetupOnceAsync(client, timeout).ConfigureAwait(false);
                await StartFakeLlmInDistroAsync().ConfigureAwait(false);
                await ApplyFakeLlmProviderAsync(client).ConfigureAwait(false);
                return;
            }
            catch (RetryableLocalSetupException ex) when (attempt < maxRetries)
            {
                lastFailure = ex;
                // Brief pause before re-running localSetup.start. The
                // production "Retry" handler also re-invokes the same engine
                // method without further user input.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        throw lastFailure ?? new InvalidOperationException(
            "DriveLocalSetupAndPrepareGatewayAsync exited without completing or failing.");
    }

    private sealed class RetryableLocalSetupException : Exception
    {
        public RetryableLocalSetupException(string message) : base(message) { }
    }

    private static async Task RunLocalSetupOnceAsync(McpClient client, TimeSpan timeout)
    {
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

        // The "Pairing Windows tray node" phase (#14) hangs in CI because:
        //   1. autopair sends node.pair.approve too eagerly and races the
        //      gateway's request-registration (gateway answers "unknown
        //      requestId"); and
        //   2. shortly after, the gateway emits a 1012 "service restart"
        //      close code, exits, and is not auto-restarted (the install
        //      doesn't register a Restart=on-failure unit).
        // We run a watchdog alongside the status poll that:
        //   - re-runs `openclaw gateway start` (idempotent) every 10s to
        //     get a crashed gateway back up;
        //   - lists pending pairings and approves them directly via the
        //     CLI's local-state fallback (which assumes operator.admin).
        // Both production bugs deserve real fixes, but this lets the
        // Plan-A scenarios make forward progress today.
        using var watchdogCts = new System.Threading.CancellationTokenSource();
        var watchdog = RunNodePairWatchdogAsync(watchdogCts.Token);

        try
        {
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
                    if (string.Equals(lastStatus, "Complete", StringComparison.OrdinalIgnoreCase))
                        return;

                    var err = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;
                    var msg = "localSetup did not Complete. status=" + lastStatus +
                              ", message=" + lastMessage + ", error=" + err;
                    if (string.Equals(lastStatus, "FailedRetryable", StringComparison.OrdinalIgnoreCase))
                        throw new RetryableLocalSetupException(msg);
                    throw new InvalidOperationException(msg);
                }
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            throw new TimeoutException(
                $"localSetup did not reach a terminal state within {timeout}. " +
                "Last status=" + lastStatus + ", message=" + lastMessage);
        }
        finally
        {
            watchdogCts.Cancel();
            try { await watchdog.ConfigureAwait(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Background watchdog: every 10s, ensure the openclaw gateway is up
    /// inside the distro and approve any pending pair requests via the
    /// CLI's local-state admin fallback. Works around two production-side
    /// flakes that block Phase 14 ("Pairing Windows tray node") in CI:
    /// the gateway service can exit on its own and the tray's autopair
    /// races the gateway's pair-request registration.
    /// </summary>
    private static readonly object _watchdogLogLock = new();
    private static string? _watchdogLogPath;
    private static string WatchdogLogPath()
    {
        if (_watchdogLogPath is not null) return _watchdogLogPath;
        var dir = Environment.GetEnvironmentVariable("GATEWAY_COMPAT_LOG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = System.IO.Path.GetTempPath();
        try { System.IO.Directory.CreateDirectory(dir); } catch { /* ignore */ }
        _watchdogLogPath = System.IO.Path.Combine(dir, "node-pair-watchdog.log");
        return _watchdogLogPath;
    }

    private static void WatchdogLog(string message)
    {
        try
        {
            lock (_watchdogLogLock)
            {
                System.IO.File.AppendAllText(
                    WatchdogLogPath(),
                    DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
        }
        catch { /* swallow */ }
    }

    private static async Task RunNodePairWatchdogAsync(System.Threading.CancellationToken ct)
    {
        // Wait so we don't trample the pre-pair install phases (~90 s).
        WatchdogLog("watchdog start; sleeping 120s before first tick");
        try { await Task.Delay(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Plan-A workaround: the in-tray `openclaw gateway start`
                // command claims success but the spawned node process
                // crashes within seconds (no Restart= unit). The
                // watchdog log shows every devices-list call returning
                // "gateway closed (1006 abnormal closure)" — so a) start
                // didn't actually keep it up, and b) nothing brings it
                // back. Spawn the gateway ourselves via nohup. Idempotent:
                // pgrep skips if a node ... gateway --port 18789 is already
                // running.
                var spawnResult = await RunWslBashAsync(
                    "if ! pgrep -f 'openclaw/dist/index.js gateway --port 18789' >/dev/null 2>&1; then " +
                      "nohup /opt/openclaw/bin/openclaw gateway --port 18789 " +
                        "> /home/openclaw/openclaw-gateway-watchdog.log 2>&1 & " +
                      "disown; " +
                    "fi").ConfigureAwait(false);
                WatchdogLog($"spawn nohup gateway: exit={spawnResult.ExitCode} stdout={Truncate(spawnResult.Stdout)} stderr={Truncate(spawnResult.Stderr)}");

                // Give the gateway a couple seconds to bind 18789 before
                // querying it.
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

                var listResult = await RunWslOpenClawAsync("devices", "list", "--json").ConfigureAwait(false);
                WatchdogLog($"devices list: exit={listResult.ExitCode} stdoutLen={listResult.Stdout?.Length ?? 0} stdout={Truncate(listResult.Stdout)} stderr={Truncate(listResult.Stderr)}");
                var pending = ParsePendingRequestIds(listResult.Stdout);
                WatchdogLog($"pending request ids: [{string.Join(",", pending)}]");
                foreach (var requestId in pending)
                {
                    if (ct.IsCancellationRequested) break;
                    var approveResult = await RunWslOpenClawAsync(
                        "devices", "approve", requestId).ConfigureAwait(false);
                    WatchdogLog($"approve {requestId}: exit={approveResult.ExitCode} stdout={Truncate(approveResult.Stdout)} stderr={Truncate(approveResult.Stderr)}");
                }
            }
            catch (Exception ex)
            {
                WatchdogLog("tick threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        WatchdogLog("watchdog cancelled, exiting");
    }

    /// <summary>
    /// Runs a raw bash command via <c>wsl -d OpenClawGateway -u openclaw -- bash -lc &lt;cmd&gt;</c>.
    /// Best-effort: never throws.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunWslBashAsync(string bashCommand)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
        psi.ArgumentList.Add(bashCommand);

        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (-1, "", "Process.Start returned null");
            var so = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var se = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return (p.ExitCode, so, se);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 500 ? s.Substring(0, 500) + "..." : s;
    }

    private static System.Collections.Generic.List<string> ParsePendingRequestIds(string? stdout)
    {
        var requestIds = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(stdout)) return requestIds;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("pending", out var pending)
                && pending.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pending.EnumerateArray())
                {
                    if (entry.TryGetProperty("requestId", out var rid)
                        && rid.ValueKind == JsonValueKind.String
                        && rid.GetString() is { Length: > 0 } id)
                    {
                        requestIds.Add(id);
                    }
                }
            }
        }
        catch (JsonException) { /* ignore */ }
        return requestIds;
    }

    /// <summary>
    /// Runs <c>wsl -d OpenClawGateway -u openclaw -- bash -lc "openclaw &lt;args&gt;"</c>.
    /// Uses a login shell so the openclaw user's PATH + env (including
    /// OPENCLAW_PROFILE / OPENCLAW_STATE_DIR) are populated the same way
    /// they would be for an interactive shell. Best-effort: never throws.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunWslOpenClawAsync(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
        // Quote each arg for the bash subshell.
        var cmd = new System.Text.StringBuilder("/opt/openclaw/bin/openclaw");
        foreach (var a in args)
        {
            cmd.Append(' ');
            cmd.Append('\'');
            cmd.Append(a.Replace("'", "'\\''", StringComparison.Ordinal));
            cmd.Append('\'');
        }
        psi.ArgumentList.Add(cmd.ToString());

        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (-1, "", "Process.Start returned null");
            var so = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var se = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return (p.ExitCode, so, se);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    /// <summary>
    /// <c>wsl --terminate OpenClawGateway</c>. Released by fixtures in
    /// DisposeAsync so the next fixture's localSetup starts against a
    /// stopped distro (otherwise it sees port 18789 already bound and
    /// reports <c>local_gateway_port_in_use</c>).
    /// </summary>
    public static async Task TerminateDistroAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--terminate");
        psi.ArgumentList.Add(DistroName);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return;
            await p.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { /* best effort */ }
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
