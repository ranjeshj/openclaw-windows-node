using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClawTray.Services.TestHooks;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit tests for the gateway-compat <c>tray.testhook.*</c> MCP tool surface.
/// The capability itself is compile-time-gated behind
/// <c>OPENCLAW_E2E_HOOKS</c>; the test project defines that constant so we can
/// exercise the class. The Release-build smoke
/// (<see cref="ReleaseBuildExcludesTestHooksTests"/>) verifies that the
/// shipped tray binary does <i>not</i> compile it.
/// </summary>
public class TestHookCapabilityTests : IDisposable
{
    private readonly string? _originalE2eEnv;

    public TestHookCapabilityTests()
    {
        _originalE2eEnv = Environment.GetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, _originalE2eEnv);
    }

    [Fact]
    public void Category_AndCommandSurface_AreStable()
    {
        var cap = NewCapability();

        Assert.Equal("tray.testhook", cap.Category);
        // Snapshot the surface so accidental rename/removal is caught.
        var expected = new[]
        {
            "tray.testhook.diagnostics.dump",
            "tray.testhook.gateway.config.patch",
            "tray.testhook.localSetup.start",
            "tray.testhook.localSetup.status",
            "tray.testhook.localSetup.cancel",
            "tray.testhook.connection.waitFor",
            "tray.testhook.pairing.reset",
            "tray.testhook.chat.send",
        };
        Assert.Equal(expected, cap.Commands.ToArray());
    }

    [Fact]
    public async Task AllTools_AreGatedBy_OPENCLAW_TRAY_E2E()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, null);
        var cap = NewCapability();

        foreach (var command in cap.Commands)
        {
            var response = await cap.ExecuteAsync(new NodeInvokeRequest { Command = command });
            Assert.False(response.Ok, $"Expected {command} to refuse without OPENCLAW_TRAY_E2E=1");
            Assert.Contains("OPENCLAW_TRAY_E2E", response.Error ?? "");
        }
    }

    [Fact]
    public async Task DiagnosticsDump_ReturnsExpectedShape_WhenEnabled()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var diagnostics = new TestHookDiagnostics(
            Connection: new { state = "connected" },
            Node: new { capabilities = new[] { "system", "device" } },
            Pairing: new { paired = true, deviceId = "dev-123" },
            SettingsSnapshot: new { enableNodeMode = true },
            Errors: Array.Empty<string>());
        var cap = NewCapability(() => diagnostics);

        var response = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "tray.testhook.diagnostics.dump",
        });

        Assert.True(response.Ok);
        Assert.NotNull(response.Payload);
        var json = JsonSerializer.Serialize(response.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(GatewayLkg.Version, root.GetProperty("gatewayLkgVersion").GetString());
        Assert.True(root.TryGetProperty("trayUptimeSeconds", out _));
        Assert.True(root.TryGetProperty("processId", out _));
        Assert.Equal("connected",
            root.GetProperty("connection").GetProperty("state").GetString());
        Assert.True(root.GetProperty("pairing").GetProperty("paired").GetBoolean());
        Assert.Equal("dev-123",
            root.GetProperty("pairing").GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task DiagnosticsDump_SurfacesProviderErrors_InsteadOfThrowing()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability(() => throw new InvalidOperationException("simulated failure"));

        var response = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "tray.testhook.diagnostics.dump",
        });

        Assert.True(response.Ok, "diagnostics.dump should never throw — it must wrap errors");
        var json = JsonSerializer.Serialize(response.Payload);
        Assert.Contains("simulated failure", json);
        Assert.Contains("diagnosticsProvider failed", json);
    }

    [Theory]
    [InlineData("tray.testhook.localSetup.start")]
    [InlineData("tray.testhook.localSetup.status")]
    [InlineData("tray.testhook.localSetup.cancel")]
    [InlineData("tray.testhook.connection.waitFor")]
    [InlineData("tray.testhook.pairing.reset")]
    [InlineData("tray.testhook.chat.send")]
    public async Task NotYetImplementedTools_FailLoudlyWithStableMessage(string command)
    {
        // The harness probes the full surface during fixture init; if it
        // silently succeeded against a not-yet-implemented tool, the
        // failure mode would be a missing assertion much later in the
        // run. The explicit "not yet implemented" failure message is
        // intentional and asserted here so a future commit that fills
        // in the tool can't accidentally regress to silent success.
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability();

        var response = await cap.ExecuteAsync(new NodeInvokeRequest { Command = command });

        Assert.False(response.Ok);
        Assert.Contains("not yet implemented", response.Error ?? "");
    }

    [Fact]
    public async Task UnknownCommand_FailsClearly()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability();

        var response = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "tray.testhook.nonexistent",
        });

        Assert.False(response.Ok);
        Assert.Contains("Unknown command", response.Error ?? "");
    }

    // -----------------------------------------------------------------------
    // gateway.config.patch
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GatewayConfigPatch_RequiresWslRunner()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability(wsl: null);

        var response = await cap.ExecuteAsync(BuildPatchRequest("OpenClawGateway", "{}"));

        Assert.False(response.Ok);
        Assert.Contains("requires an IWslCommandRunner", response.Error ?? "");
    }

    [Fact]
    public async Task GatewayConfigPatch_RequiresDistroName()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability(wsl: new FakeWslRunner());

        var response = await cap.ExecuteAsync(BuildPatchRequest(distroName: "", "{}"));

        Assert.False(response.Ok);
        Assert.Contains("'distroName' is required", response.Error ?? "");
    }

    [Fact]
    public async Task GatewayConfigPatch_RequiresPatchJson()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var cap = NewCapability(wsl: new FakeWslRunner());

        var response = await cap.ExecuteAsync(BuildPatchRequest("OpenClawGateway", patchJson: ""));

        Assert.False(response.Ok);
        Assert.Contains("'patchJson' is required", response.Error ?? "");
    }

    [Fact]
    public async Task GatewayConfigPatch_WritesPatch_RunsConfigPatch_RunsConfigValidate()
    {
        // Same-path verification: the hook must invoke the same `openclaw config
        // patch --file ... && openclaw config validate` sequence the user runs
        // by hand (per docs/GATEWAY_COMPAT_TESTING.md). This test asserts the
        // exact command sequence rather than mocking behavior so a refactor
        // can't quietly change what reaches WSL.
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var fake = new FakeWslRunner();
        var cap = NewCapability(wsl: fake);
        const string patch = "{ models: { providers: { fake: { api: \"openai-completions\" } } } }";

        var response = await cap.ExecuteAsync(BuildPatchRequest("MyDistro", patch));

        Assert.True(response.Ok, response.Error);
        var payload = JsonSerializer.Serialize(response.Payload);
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.GetProperty("writeOk").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("patchOk").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("validateOk").GetBoolean());

        // Exact command sequence assertions:
        Assert.Equal(3, fake.Calls.Count);
        // 1: cat patch into the WSL filesystem (base64-decoded).
        Assert.Equal("MyDistro", fake.Calls[0].Distro);
        Assert.Equal(new[] { "-u", "openclaw", "--", "bash", "-lc" }, fake.Calls[0].Args[..5]);
        Assert.Contains("base64 -d", fake.Calls[0].Args[5]);
        Assert.Contains("/home/openclaw/openclaw.patch.json5", fake.Calls[0].Args[5]);
        // The decoded base64 must equal the original patch bytes.
        var base64Match = System.Text.RegularExpressions.Regex.Match(
            fake.Calls[0].Args[5], @"echo '([^']+)' \| base64");
        Assert.True(base64Match.Success);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64Match.Groups[1].Value));
        Assert.Equal(patch, decoded);
        // 2: openclaw config patch --file
        Assert.Equal(
            new[] { "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw",
                    "config", "patch", "--file", "/home/openclaw/openclaw.patch.json5" },
            fake.Calls[1].Args);
        // 3: openclaw config validate
        Assert.Equal(
            new[] { "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "config", "validate" },
            fake.Calls[2].Args);
    }

    [Fact]
    public async Task GatewayConfigPatch_ReturnsValidateFailure_WithoutThrowing()
    {
        // When validate fails, the hook must return Ok=true with the payload
        // so the harness can inspect WHY the gateway rejected the config.
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var fake = new FakeWslRunner
        {
            ValidateExitCode = 2,
            ValidateStderr = "schema error: models.providers.fake.api must be one of [...]",
        };
        var cap = NewCapability(wsl: fake);

        var response = await cap.ExecuteAsync(BuildPatchRequest("MyDistro", "{ bogus: true }"));

        Assert.True(response.Ok, response.Error);
        var json = JsonSerializer.Serialize(response.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("writeOk").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("patchOk").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("validateOk").GetBoolean());
        Assert.Contains("schema error", doc.RootElement.GetProperty("validateStderr").GetString() ?? "");
    }

    [Fact]
    public async Task GatewayConfigPatch_SkipsPatchAndValidate_WhenWriteFails()
    {
        Environment.SetEnvironmentVariable(
            TestHookCapability.RuntimeEnabledEnvironmentVariable, "1");
        var fake = new FakeWslRunner { WriteExitCode = 1, WriteStderr = "disk full" };
        var cap = NewCapability(wsl: fake);

        var response = await cap.ExecuteAsync(BuildPatchRequest("MyDistro", "{}"));

        Assert.True(response.Ok, response.Error);
        var json = JsonSerializer.Serialize(response.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("writeOk").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("patchOk").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("validateOk").GetBoolean());
        // Only the write call should have run.
        Assert.Single(fake.Calls);
    }

    private static NodeInvokeRequest BuildPatchRequest(string distroName, string patchJson)
    {
        var json = JsonSerializer.Serialize(new { distroName, patchJson });
        return new NodeInvokeRequest
        {
            Command = "tray.testhook.gateway.config.patch",
            Args = JsonDocument.Parse(json).RootElement,
        };
    }

    private static TestHookCapability NewCapability(
        Func<TestHookDiagnostics>? diagnostics = null,
        IWslCommandRunner? wsl = null) =>
        new(new NullLogger(), diagnostics ?? TestHookDiagnostics.Empty, wsl);

    /// <summary>
    /// Records every WSL call the capability makes, lets the test scenario
    /// control exit codes per phase. Keeps the unit test deterministic and
    /// avoids needing real WSL on the dev machine.
    /// </summary>
    private sealed class FakeWslRunner : IWslCommandRunner
    {
        public List<(string Distro, string[] Args)> Calls { get; } = new();
        public int WriteExitCode { get; set; }
        public string WriteStderr { get; set; } = string.Empty;
        public int PatchExitCode { get; set; }
        public string PatchStdout { get; set; } = string.Empty;
        public int ValidateExitCode { get; set; }
        public string ValidateStderr { get; set; } = string.Empty;

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var argsArray = command.ToArray();
            Calls.Add((name, argsArray));
            // Heuristic: classify by the command shape (matches the hook's
            // three phases). This is the only place test order matters.
            var joined = string.Join(" ", argsArray);
            if (joined.Contains("base64 -d"))
                return Task.FromResult(new WslCommandResult(WriteExitCode, string.Empty, WriteStderr));
            if (joined.Contains("config patch"))
                return Task.FromResult(new WslCommandResult(PatchExitCode, PatchStdout, string.Empty));
            if (joined.Contains("config validate"))
                return Task.FromResult(new WslCommandResult(ValidateExitCode, string.Empty, ValidateStderr));
            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WslDistroInfo>>(Array.Empty<WslDistroInfo>());
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
    }
}

