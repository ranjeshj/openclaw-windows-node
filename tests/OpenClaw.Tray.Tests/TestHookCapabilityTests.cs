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
/// Pruned (2026-05-19) to the minimum set that the E2E harness cannot
/// replace:
/// <list type="bullet">
///   <item>The runtime <c>OPENCLAW_TRAY_E2E</c> gate (security invariant —
///         E2E always sets the var so it can't prove the gate works).</item>
///   <item>Exact command-sequence assertions for hooks that delegate to
///         WSL (the same-path rule needs verifying at this level).</item>
///   <item>Failure-mode assertions that are slow or impractical to drive
///         from real WSL/openclaw.</item>
/// </list>
/// Surface stability and diagnostics shape are covered by
/// <c>OpenClaw.GatewayCompat.E2ETests.HarnessSmokeTests</c>. The
/// Release-build smoke (<see cref="ReleaseBuildExcludesTestHooksTests"/>)
/// is the only check that the shipped tray binary doesn't contain this
/// type at all.
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
    public async Task AllTools_AreGatedBy_OPENCLAW_TRAY_E2E()
    {
        // Security invariant: even when the type is present in the binary,
        // every command must refuse without the env var set. E2E can't
        // prove this — it always sets OPENCLAW_TRAY_E2E=1.
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
    // gateway.config.patch — kept because the same-path rule needs exact
    // command-sequence verification, and write/validate failure modes are
    // expensive to drive from real WSL.
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
    public async Task GatewayConfigPatch_WritesPatch_RunsConfigPatch_RunsConfigValidate()
    {
        // Same-path verification: the hook must invoke the same `openclaw config
        // patch --file ... && openclaw config validate` sequence the user runs
        // by hand. Asserts the exact command sequence so a refactor can't
        // quietly change what reaches WSL.
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

        Assert.Equal(3, fake.Calls.Count);
        // Call 0: write the patch file via bash + base64.
        // Match the production "explicit -u" pattern (LocalGatewaySetup.cs:993):
        //   wsl -d <distro> -u <user> -- bash -lc <script>
        Assert.Equal(new[] { "-d", "MyDistro", "-u", "openclaw", "--", "bash", "-lc" }, fake.Calls[0].Args[..7]);
        Assert.Contains("base64 -d", fake.Calls[0].Args[7]);
        Assert.Contains("/home/openclaw/openclaw.patch.json5", fake.Calls[0].Args[7]);
        var base64Match = System.Text.RegularExpressions.Regex.Match(
            fake.Calls[0].Args[7], @"echo '([^']+)' \| base64");
        Assert.True(base64Match.Success);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64Match.Groups[1].Value));
        Assert.Equal(patch, decoded);
        // Call 1: openclaw config patch --file <path>
        Assert.Equal(
            new[] { "-d", "MyDistro", "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw",
                    "config", "patch", "--file", "/home/openclaw/openclaw.patch.json5" },
            fake.Calls[1].Args);
        // Call 2: openclaw config validate
        Assert.Equal(
            new[] { "-d", "MyDistro", "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "config", "validate" },
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
        // Driving a write failure from real WSL is hard (fill the disk?);
        // unit test is the right level.
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

    private sealed class FakeWslRunner : IWslCommandRunner
    {
        public List<(string Distro, string[] Args)> Calls { get; } = new();
        public int WriteExitCode { get; set; }
        public string WriteStderr { get; set; } = string.Empty;
        public int PatchExitCode { get; set; }
        public string PatchStdout { get; set; } = string.Empty;
        public int ValidateExitCode { get; set; }
        public string ValidateStderr { get; set; } = string.Empty;

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            // The hook switched from RunInDistroAsync to RunAsync directly so
            // the "-u <user>" arg sits before the "--" separator. Distro name
            // is the value after "-d". Extract it for test convenience.
            var argsArray = arguments.ToArray();
            var distro = "";
            for (var i = 0; i < argsArray.Length - 1; i++)
            {
                if (argsArray[i] == "-d") { distro = argsArray[i + 1]; break; }
            }
            Calls.Add((distro, argsArray));
            var joined = string.Join(" ", argsArray);
            if (joined.Contains("base64 -d"))
                return Task.FromResult(new WslCommandResult(WriteExitCode, string.Empty, WriteStderr));
            if (joined.Contains("config patch"))
                return Task.FromResult(new WslCommandResult(PatchExitCode, PatchStdout, string.Empty));
            if (joined.Contains("config validate"))
                return Task.FromResult(new WslCommandResult(ValidateExitCode, string.Empty, ValidateStderr));
            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            // Not used by the hook anymore, but kept for interface compliance.
            // If a future hook delegates here, recording would still work.
            Calls.Add((name, command.ToArray()));
            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WslDistroInfo>>(Array.Empty<WslDistroInfo>());
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
    }
}
