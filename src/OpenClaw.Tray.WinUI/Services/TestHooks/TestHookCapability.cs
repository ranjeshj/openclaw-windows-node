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
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services.LocalGatewaySetup;

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
        "tray.testhook.diagnostics.dump",
        // Harness-only primitive (no UI equivalent — gateway config setup is
        // normally handled by LocalGatewaySetup's GatewayConfigurationPreparer
        // which writes a fixed loopback-only config; the patch hook lets the
        // harness inject the fake-LLM provider on top via the same `openclaw
        // config patch --file` CLI the user can run by hand).
        "tray.testhook.gateway.config.patch",
        // localSetup.* wraps App.CreateLocalGatewaySetupEngine().RunLocalOnlyAsync
        // — same method LocalSetupProgressPage's "Set up locally" handler calls.
        "tray.testhook.localSetup.start",
        "tray.testhook.localSetup.status",
        "tray.testhook.localSetup.cancel",
        // connection.waitFor observes GatewayConnectionManager (the same
        // singleton every UI surface observes — see docs/CONNECTION_ARCHITECTURE.md).
        "tray.testhook.connection.waitFor",
        // pairing.reset goes through GatewayRegistry.RemoveGateway — same
        // method the Settings page "Reset pairing" button calls.
        "tray.testhook.pairing.reset",
        // chat.send goes through OpenClawChatDataProvider.SendMessageAsync —
        // same method ChatWindow.OnSendClicked invokes.
        "tray.testhook.chat.send",
    };

    private readonly Func<TestHookDiagnostics> _diagnosticsProvider;
    private readonly IWslCommandRunner? _wslRunner;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public TestHookCapability(
        IOpenClawLogger logger,
        Func<TestHookDiagnostics> diagnosticsProvider,
        IWslCommandRunner? wslRunner = null)
        : base(logger)
    {
        _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
        _wslRunner = wslRunner;
    }

    public static bool IsRuntimeEnabled() =>
        Environment.GetEnvironmentVariable(RuntimeEnabledEnvironmentVariable) == "1";

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        if (!IsRuntimeEnabled())
        {
            return Error("tray.testhook.* tools are gated by OPENCLAW_TRAY_E2E=1 at runtime");
        }

        switch (request.Command)
        {
            case "tray.testhook.diagnostics.dump":
                return Success(BuildDiagnosticsPayload());

            case "tray.testhook.gateway.config.patch":
                return await GatewayConfigPatchAsync(request.Args).ConfigureAwait(false);

            case "tray.testhook.localSetup.start":
            case "tray.testhook.localSetup.status":
            case "tray.testhook.localSetup.cancel":
            case "tray.testhook.connection.waitFor":
            case "tray.testhook.pairing.reset":
            case "tray.testhook.chat.send":
                return Error($"{request.Command} is declared but not yet implemented (W4 follow-up)");

            default:
                return Error($"Unknown command: {request.Command}");
        }
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

    // -----------------------------------------------------------------------
    // tray.testhook.gateway.config.patch
    //
    // Writes a JSON5 patch (typically the fake-LLM provider) into the WSL
    // distro and runs `openclaw config patch --file <path>` followed by
    // `openclaw config validate`. Mirrors what a power user would do by
    // hand per docs/GATEWAY_COMPAT_TESTING.md and tools/fake-llm-server/README.md.
    //
    // Same-path notes: there is no UI equivalent for free-form gateway
    // config patching (the tray's own GatewayConfigurationPreparer writes
    // a fixed loopback-only config). This hook is a harness primitive
    // that uses the same `openclaw config patch` + `openclaw config validate`
    // CLI surface the user can invoke from the command line — and the same
    // IWslCommandRunner the tray uses for every other WSL operation.
    //
    // Args:
    //   { distroName: string,
    //     openclawBinPath?: string,    (default "/opt/openclaw/bin/openclaw")
    //     patchJson: string,           (JSON5 patch body — required)
    //     patchPath?: string,          (default "/home/openclaw/openclaw.patch.json5")
    //     wslUser?: string             (default "openclaw")
    //   }
    // Returns:
    //   { writeOk, writeStderr, patchOk, patchStdout, patchStderr,
    //     validateOk, validateStdout, validateStderr, patchPath }
    // -----------------------------------------------------------------------
    private async Task<NodeInvokeResponse> GatewayConfigPatchAsync(JsonElement args)
    {
        if (_wslRunner is null)
        {
            return Error("gateway.config.patch requires an IWslCommandRunner — not provided by host");
        }

        var distroName = GetStringArg(args, "distroName");
        if (string.IsNullOrWhiteSpace(distroName))
        {
            return Error("gateway.config.patch: 'distroName' is required");
        }

        var patchJson = GetStringArg(args, "patchJson");
        if (string.IsNullOrWhiteSpace(patchJson))
        {
            return Error("gateway.config.patch: 'patchJson' is required");
        }

        var openclawBin = GetStringArg(args, "openclawBinPath") ?? "/opt/openclaw/bin/openclaw";
        var patchPath = GetStringArg(args, "patchPath") ?? "/home/openclaw/openclaw.patch.json5";
        var wslUser = GetStringArg(args, "wslUser") ?? "openclaw";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // Write the patch to the WSL filesystem via `cat > <path>` over stdin.
        // We use a base64 round-trip to sidestep shell-quoting headaches in
        // multi-line JSON5; `openclaw config patch` accepts JSON5 with
        // newlines and comments.
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(patchJson));
        var writeScript = $"echo {ShellEscape(base64)} | base64 -d > {ShellEscape(patchPath)}";
        var writeResult = await _wslRunner.RunInDistroAsync(
            distroName,
            new[] { "-u", wslUser, "--", "bash", "-lc", writeScript },
            cts.Token).ConfigureAwait(false);

        // openclaw config patch --file <path>
        WslCommandResult? patchResult = null;
        WslCommandResult? validateResult = null;
        if (writeResult.Success)
        {
            patchResult = await _wslRunner.RunInDistroAsync(
                distroName,
                new[] { "-u", wslUser, "--", openclawBin, "config", "patch", "--file", patchPath },
                cts.Token).ConfigureAwait(false);

            if (patchResult.Success)
            {
                validateResult = await _wslRunner.RunInDistroAsync(
                    distroName,
                    new[] { "-u", wslUser, "--", openclawBin, "config", "validate" },
                    cts.Token).ConfigureAwait(false);
            }
        }

        var payload = new
        {
            writeOk = writeResult.Success,
            writeStderr = writeResult.StandardError,
            patchOk = patchResult?.Success ?? false,
            patchStdout = patchResult?.StandardOutput,
            patchStderr = patchResult?.StandardError,
            validateOk = validateResult?.Success ?? false,
            validateStdout = validateResult?.StandardOutput,
            validateStderr = validateResult?.StandardError,
            patchPath,
        };

        // Even when the gateway rejects the patch (validate fails), return
        // Ok=true with the payload so the harness can inspect WHY rather
        // than getting back an opaque "failed" string. The test asserts on
        // payload.validateOk == true.
        return Success(payload);
    }

    /// <summary>
    /// Single-quote a value for embedding in a bash command, escaping any
    /// embedded single quotes.
    /// </summary>
    private static string ShellEscape(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
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
