#if OPENCLAW_E2E_HOOKS

// ============================================================================
//  Gateway-compat E2E test hooks.
//
//  Compiled ONLY when MSBuild property OpenClawEnableTestHooks=true
//  (see OpenClaw.Tray.WinUI.csproj). Production binaries must not contain
//  this type - enforced by
//  OpenClaw.Tray.Tests.ReleaseBuildExcludesTestHooksTests.
//
//  These tools are exposed over the local MCP HTTP server only. They are
//  NOT registered on the gateway WindowsNodeClient.
//
//  RULE: same-path-as-user. Every tool MUST end up invoking the same
//  method the matching UI click handler invokes. See per-tool comments
//  for the mapping. Do NOT reimplement parallel logic here.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Services.TestHooks;

internal sealed class TestHookCapability : NodeCapabilityBase
{
    public const string RuntimeEnabledEnvironmentVariable = "OPENCLAW_TRAY_E2E";

    public override string Category => "tray.testhook";

    public override IReadOnlyList<string> Commands { get; } = new[]
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

    private readonly Func<TestHookDiagnostics> _diagnosticsProvider;
    private readonly IWslCommandRunner? _wslRunner;
    private readonly ITestHookHost? _host;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private ITestHookLocalSetupRun? _activeLocalSetupRun;

    public TestHookCapability(
        IOpenClawLogger logger,
        Func<TestHookDiagnostics> diagnosticsProvider,
        IWslCommandRunner? wslRunner = null,
        ITestHookHost? host = null)
        : base(logger)
    {
        _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
        _wslRunner = wslRunner;
        _host = host;
    }

    public static bool IsRuntimeEnabled() =>
        Environment.GetEnvironmentVariable(RuntimeEnabledEnvironmentVariable) == "1";

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        if (!IsRuntimeEnabled())
            return Error("tray.testhook.* tools are gated by OPENCLAW_TRAY_E2E=1 at runtime");

        switch (request.Command)
        {
            case "tray.testhook.diagnostics.dump":     return Success(BuildDiagnosticsPayload());
            case "tray.testhook.gateway.config.patch": return await GatewayConfigPatchAsync(request.Args).ConfigureAwait(false);
            case "tray.testhook.connection.waitFor":   return await ConnectionWaitForAsync(request.Args).ConfigureAwait(false);
            case "tray.testhook.pairing.reset":        return PairingReset(request.Args);
            case "tray.testhook.chat.send":            return await ChatSendAsync(request.Args).ConfigureAwait(false);
            case "tray.testhook.localSetup.start":     return LocalSetupStart(request.Args);
            case "tray.testhook.localSetup.status":    return LocalSetupStatus();
            case "tray.testhook.localSetup.cancel":    return LocalSetupCancel();
            default:                                   return Error($"Unknown command: {request.Command}");
        }
    }

    private object BuildDiagnosticsPayload()
    {
        TestHookDiagnostics snapshot;
        try { snapshot = _diagnosticsProvider(); }
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

    // gateway.config.patch - same-path: openclaw config patch + validate CLI
    private async Task<NodeInvokeResponse> GatewayConfigPatchAsync(JsonElement args)
    {
        if (_wslRunner is null) return Error("gateway.config.patch requires an IWslCommandRunner - not provided by host");
        var distroName = GetStringArg(args, "distroName");
        if (string.IsNullOrWhiteSpace(distroName)) return Error("gateway.config.patch: 'distroName' is required");
        var patchJson = GetStringArg(args, "patchJson");
        if (string.IsNullOrWhiteSpace(patchJson)) return Error("gateway.config.patch: 'patchJson' is required");
        var openclawBin = GetStringArg(args, "openclawBinPath") ?? "/opt/openclaw/bin/openclaw";
        var patchPath = GetStringArg(args, "patchPath") ?? "/home/openclaw/openclaw.patch.json5";
        var wslUser = GetStringArg(args, "wslUser") ?? "openclaw";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(patchJson));
        var writeScript = $"echo {ShellEscape(base64)} | base64 -d > {ShellEscape(patchPath)}";
        var writeResult = await _wslRunner.RunInDistroAsync(distroName,
            new[] { "-u", wslUser, "--", "bash", "-lc", writeScript }, cts.Token).ConfigureAwait(false);

        WslCommandResult? patchResult = null, validateResult = null;
        if (writeResult.Success)
        {
            patchResult = await _wslRunner.RunInDistroAsync(distroName,
                new[] { "-u", wslUser, "--", openclawBin, "config", "patch", "--file", patchPath },
                cts.Token).ConfigureAwait(false);
            if (patchResult.Success)
                validateResult = await _wslRunner.RunInDistroAsync(distroName,
                    new[] { "-u", wslUser, "--", openclawBin, "config", "validate" },
                    cts.Token).ConfigureAwait(false);
        }

        return Success(new
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
        });
    }

    // connection.waitFor - same-path: subscribes to IGatewayConnectionManager.StateChanged,
    // the same event the tray icon and ConnectionPage observe.
    private async Task<NodeInvokeResponse> ConnectionWaitForAsync(JsonElement args)
    {
        if (_host?.ConnectionManager is null)
            return Error("connection.waitFor requires a host with a ConnectionManager");
        var manager = _host.ConnectionManager;
        var targetOverallState = GetStringArg(args, "targetOverallState");
        var operatorConnected = TryGetBool(args, "operatorConnected");
        var nodeConnected = TryGetBool(args, "nodeConnected");
        var paired = TryGetBool(args, "paired");
        var timeoutSeconds = GetArg<int?>(args, "timeoutSeconds") ?? 30;

        if (targetOverallState is null && operatorConnected is null && nodeConnected is null && paired is null)
            return Error("connection.waitFor: at least one predicate (targetOverallState/operatorConnected/nodeConnected/paired) is required");

        bool Matches(OpenClaw.Connection.GatewayConnectionSnapshot s)
        {
            if (targetOverallState is not null &&
                !string.Equals(s.OverallState.ToString(), targetOverallState, StringComparison.OrdinalIgnoreCase))
                return false;
            if (operatorConnected is bool oc &&
                (s.OperatorState == OpenClaw.Connection.RoleConnectionState.Connected) != oc)
                return false;
            if (nodeConnected is bool nc &&
                (s.NodeState == OpenClaw.Connection.RoleConnectionState.Connected) != nc)
                return false;
            if (paired is bool p && (s.NodePairingStatus == OpenClaw.Shared.PairingStatus.Paired) != p)
                return false;
            return true;
        }

        var tcs = new TaskCompletionSource<OpenClaw.Connection.GatewayConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, OpenClaw.Connection.GatewayConnectionSnapshot s)
        {
            if (Matches(s)) tcs.TrySetResult(s);
        }
        manager.StateChanged += Handler;
        try
        {
            var current = manager.CurrentSnapshot;
            if (Matches(current)) return Success(SerializeSnapshot(current, reached: true));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
            try
            {
                var snap = await tcs.Task.ConfigureAwait(false);
                return Success(SerializeSnapshot(snap, reached: true));
            }
            catch (OperationCanceledException)
            {
                return Success(SerializeSnapshot(manager.CurrentSnapshot, reached: false));
            }
        }
        finally { manager.StateChanged -= Handler; }
    }

    private static object SerializeSnapshot(OpenClaw.Connection.GatewayConnectionSnapshot s, bool reached) => new
    {
        reached,
        overallState = s.OverallState.ToString(),
        operatorState = s.OperatorState.ToString(),
        nodeState = s.NodeState.ToString(),
        nodePairingStatus = s.NodePairingStatus.ToString(),
        operatorPairingRequired = s.OperatorPairingRequired,
        operatorDeviceId = s.OperatorDeviceId,
        nodeDeviceId = s.NodeDeviceId,
        gatewayId = s.GatewayId,
        gatewayUrl = s.GatewayUrl,
        gatewayName = s.GatewayName,
        operatorError = s.OperatorError,
        nodeError = s.NodeError,
    };

    // pairing.reset - same-path: GatewayRegistry.Remove + per-gateway identity wipe
    private NodeInvokeResponse PairingReset(JsonElement args)
    {
        if (_host?.Registry is null) return Error("pairing.reset requires a host with a GatewayRegistry");
        var registry = _host.Registry;
        var gatewayId = GetStringArg(args, "gatewayId");
        var wipeFiles = TryGetBool(args, "wipeIdentityFiles") ?? true;

        var removedIds = new List<string>();
        if (string.IsNullOrEmpty(gatewayId))
        {
            foreach (var rec in registry.GetAll().ToList())
            {
                registry.Remove(rec.Id);
                removedIds.Add(rec.Id);
            }
        }
        else
        {
            registry.Remove(gatewayId);
            removedIds.Add(gatewayId);
        }

        var wipedPaths = new List<string>();
        if (wipeFiles && _host.LocalAppDataDir is { } dataDir)
        {
            var gatewaysRoot = Path.Combine(dataDir, "OpenClawTray", "gateways");
            if (Directory.Exists(gatewaysRoot))
            {
                foreach (var id in removedIds)
                {
                    var dir = Path.Combine(gatewaysRoot, id);
                    if (Directory.Exists(dir))
                    {
                        try { Directory.Delete(dir, recursive: true); wipedPaths.Add(dir); }
                        catch (Exception ex) { Logger.Warn($"pairing.reset: failed to wipe {dir}: {ex.Message}"); }
                    }
                }
            }
            if (string.IsNullOrEmpty(gatewayId))
            {
                var gatewaysJson = Path.Combine(dataDir, "OpenClawTray", "gateways.json");
                if (File.Exists(gatewaysJson))
                {
                    try { File.Delete(gatewaysJson); wipedPaths.Add(gatewaysJson); }
                    catch (Exception ex) { Logger.Warn($"pairing.reset: failed to delete {gatewaysJson}: {ex.Message}"); }
                }
            }
        }

        return Success(new { removedGatewayIds = removedIds, wipedPaths });
    }

    // chat.send - same-path: OpenClawChatDataProvider.SendMessageAsync
    private async Task<NodeInvokeResponse> ChatSendAsync(JsonElement args)
    {
        if (_host?.ChatProvider is null)
            return Error("chat.send requires a host with a ChatProvider (is the chat UI initialized?)");
        var threadId = GetStringArg(args, "threadId");
        if (string.IsNullOrWhiteSpace(threadId)) return Error("chat.send: 'threadId' is required");
        var message = GetStringArg(args, "message");
        if (string.IsNullOrEmpty(message)) return Error("chat.send: 'message' is required");
        var timeoutSeconds = GetArg<int?>(args, "timeoutSeconds") ?? 60;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await _host.ChatProvider.SendMessageAsync(threadId, message, cts.Token).ConfigureAwait(false);
            return Success(new { sent = true, threadId });
        }
        catch (OperationCanceledException)
        {
            return Success(new { sent = false, threadId, timedOut = true, timeoutSeconds });
        }
        catch (Exception ex)
        {
            return Success(new { sent = false, threadId, error = ex.Message });
        }
    }

    // localSetup.* - same-path: App.CreateLocalGatewaySetupEngine().RunLocalOnlyAsync
    private NodeInvokeResponse LocalSetupStart(JsonElement args)
    {
        if (_host is null) return Error("localSetup.start requires a host");
        if (_activeLocalSetupRun is { } existing && !existing.Completion.IsCompleted)
            return Error($"localSetup.start: a run is already in progress (runId={existing.RunId})");
        var replace = TryGetBool(args, "replaceExistingConfigurationConfirmed") ?? false;
        var run = _host.StartLocalSetup(replace);
        if (run is null)
            return Error("localSetup.start: host could not create the setup engine " +
                         "(ConnectionManager / Registry / GatewayService not initialized?)");
        _activeLocalSetupRun = run;
        return Success(new { runId = run.RunId, started = true });
    }

    private NodeInvokeResponse LocalSetupStatus()
    {
        if (_activeLocalSetupRun is null) return Success(new { runId = (string?)null, status = "idle" });
        var status = _activeLocalSetupRun.GetStatus();
        return Success(new
        {
            runId = _activeLocalSetupRun.RunId,
            phase = status.Phase,
            status = status.Status,
            message = status.Message,
            isTerminal = status.IsTerminal,
            errorMessage = status.ErrorMessage,
            completed = _activeLocalSetupRun.Completion.IsCompleted,
            faulted = _activeLocalSetupRun.Completion.IsFaulted,
        });
    }

    private NodeInvokeResponse LocalSetupCancel()
    {
        if (_activeLocalSetupRun is null) return Success(new { cancelled = false, reason = "no active run" });
        _activeLocalSetupRun.Cancel();
        return Success(new { cancelled = true, runId = _activeLocalSetupRun.RunId });
    }

    private static string ShellEscape(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool? TryGetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
    }
}

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
