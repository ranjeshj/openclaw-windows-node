using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
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
    [InlineData("tray.testhook.gateway.config.patch")]
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

    private static TestHookCapability NewCapability(Func<TestHookDiagnostics>? diagnostics = null) =>
        new(new NullLogger(), diagnostics ?? TestHookDiagnostics.Empty);
}
