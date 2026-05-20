#if OPENCLAW_E2E_HOOKS

// ============================================================================
//  Gateway-compat E2E test hooks.
//
//  Compiled ONLY when MSBuild property OpenClawEnableTestHooks=true
//  (see OpenClaw.Tray.WinUI.csproj). Production binaries must not contain
//  this type — enforced by
//  OpenClaw.Tray.Tests.ReleaseBuildExcludesTestHooksTests.
//
//  These tools are exposed over the local MCP HTTP server only. They are
//  NOT registered on the gateway WindowsNodeClient (see
//  NodeService.RegisterTestHookCapability), so a misbehaving gateway can
//  never trigger them.
//
//  ===========================================================================
//  RULE: same-path-as-user. (User input, 2026-05-19)
//
//  Every tool in this capability MUST end up invoking the same method the
//  matching UI click handler invokes. If the UI handler does X inline,
//  extract X into a shared service method first and have BOTH the handler
//  and this tool call that method. Do NOT reimplement "roughly the same
//  thing" here — that defeats the entire purpose of gateway-compat
//  testing (a test that passes against a parallel implementation tells us
//  nothing about whether the real UI path still works).
//
//  Each tool implementation must include a comment that names the UI
//  caller and the shared method, so future refactors can't drift.
//  ===========================================================================
//
//  Tool surface (W3.2 — diagnostics.dump implemented, rest declared with
//  NotImplementedYet so the harness can probe the surface). Later commits
//  add the real implementations.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services.TestHooks;

internal sealed class TestHookCapability : NodeCapabilityBase
{
    /// <summary>
    /// Belt-and-suspenders second gate. Compile-time gating (this whole file)
    /// is the primary defence; this env var lets ops disable the hooks in an
    /// E2E build at runtime without rebuilding. Defaults to enabled when the
    /// type exists (you have to have asked for it at compile time anyway).
    /// </summary>
    public const string RuntimeEnabledEnvironmentVariable = "OPENCLAW_TRAY_E2E";

    public override string Category => "tray.testhook";

    public override IReadOnlyList<string> Commands { get; } = new[]
    {
        // Diagnostic — implemented in this commit.
        "tray.testhook.diagnostics.dump",
        // Gateway config — implemented in a follow-up; writes a JSON5 patch
        // into the WSL distro and runs `openclaw config validate`.
        "tray.testhook.gateway.config.patch",
        // Local setup orchestration — wraps LocalGatewaySetupEngine.
        "tray.testhook.localSetup.start",
        "tray.testhook.localSetup.status",
        "tray.testhook.localSetup.cancel",
        // Connection lifecycle.
        "tray.testhook.connection.waitFor",
        "tray.testhook.pairing.reset",
        // Chat round-trip.
        "tray.testhook.chat.send",
    };

    private readonly Func<TestHookDiagnostics> _diagnosticsProvider;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public TestHookCapability(IOpenClawLogger logger, Func<TestHookDiagnostics> diagnosticsProvider)
        : base(logger)
    {
        _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
    }

    public static bool IsRuntimeEnabled() =>
        Environment.GetEnvironmentVariable(RuntimeEnabledEnvironmentVariable) == "1";

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        if (!IsRuntimeEnabled())
        {
            return Task.FromResult(Error(
                "tray.testhook.* tools are gated by OPENCLAW_TRAY_E2E=1 at runtime"));
        }

        NodeInvokeResponse response = request.Command switch
        {
            "tray.testhook.diagnostics.dump" => Success(BuildDiagnosticsPayload()),

            "tray.testhook.gateway.config.patch" or
            "tray.testhook.localSetup.start" or
            "tray.testhook.localSetup.status" or
            "tray.testhook.localSetup.cancel" or
            "tray.testhook.connection.waitFor" or
            "tray.testhook.pairing.reset" or
            "tray.testhook.chat.send"
                => Error($"{request.Command} is declared but not yet implemented (W3.2 follow-up)"),

            _ => Error($"Unknown command: {request.Command}"),
        };

        return Task.FromResult(response);
    }

    private object BuildDiagnosticsPayload()
    {
        TestHookDiagnostics snapshot;
        try
        {
            snapshot = _diagnosticsProvider();
        }
        catch (Exception ex)
        {
            Logger.Warn($"TestHookCapability diagnosticsProvider threw: {ex.Message}");
            snapshot = TestHookDiagnostics.Unavailable(ex.Message);
        }

        return new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            trayUptimeSeconds = (DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds,
            processId = Environment.ProcessId,
            machineName = Environment.MachineName,
            gatewayLkgVersion = GatewayLkg.Version,
            gatewayVersionOverride = Environment.GetEnvironmentVariable(GatewayLkg.VersionOverrideEnvironmentVariable),
            connection = snapshot.Connection,
            node = snapshot.Node,
            pairing = snapshot.Pairing,
            settingsSnapshot = snapshot.SettingsSnapshot,
            errors = snapshot.Errors,
        };
    }
}

/// <summary>
/// Pure-data snapshot the host supplies to <see cref="TestHookCapability"/>
/// each time <c>tray.testhook.diagnostics.dump</c> is called. Keeping this
/// a record-of-data (no live objects) makes the capability deterministically
/// unit-testable in isolation.
/// </summary>
internal sealed record TestHookDiagnostics(
    object? Connection,
    object? Node,
    object? Pairing,
    object? SettingsSnapshot,
    IReadOnlyList<string> Errors)
{
    public static TestHookDiagnostics Empty() =>
        new(null, null, null, null, Array.Empty<string>());

    public static TestHookDiagnostics Unavailable(string reason) =>
        new(null, null, null, null, new[] { "diagnosticsProvider failed: " + reason });
}

#endif
