#if OPENCLAW_E2E_HOOKS

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services.TestHooks;

/// <summary>
/// Aggregates the App-level dependencies the test-hook capability needs to
/// invoke the SAME methods the UI click handlers invoke (per the
/// same-path-as-user rule documented in
/// <c>docs/GATEWAY_COMPAT_TESTING.md</c>).
///
/// Implemented by <c>App</c> (in App.xaml.cs, behind
/// <c>#if OPENCLAW_E2E_HOOKS</c>). Unit tests pass a hand-rolled fake
/// rather than instantiate App.
///
/// Each member is nullable because some hosts (the unit-test host, the
/// MCP-only mode) don't have every dependency wired. The capability
/// returns an explicit "requires X" error when a missing dependency is
/// hit, which keeps the failure mode explicit instead of silently
/// no-op-ing.
/// </summary>
internal interface ITestHookHost
{
    /// <summary>
    /// The same <see cref="OpenClaw.Connection.IGatewayConnectionManager"/>
    /// every UI surface observes for connection state.
    /// </summary>
    OpenClaw.Connection.IGatewayConnectionManager? ConnectionManager { get; }

    /// <summary>
    /// The same <see cref="OpenClaw.Connection.GatewayRegistry"/> the Settings
    /// page "Reset pairing" button mutates.
    /// </summary>
    OpenClaw.Connection.GatewayRegistry? Registry { get; }

    /// <summary>
    /// The same <see cref="OpenClawTray.Chat.OpenClawChatDataProvider"/> the
    /// ChatWindow uses to send messages.
    /// </summary>
    OpenClawTray.Chat.OpenClawChatDataProvider? ChatProvider { get; }

    /// <summary>
    /// Root directory used for per-gateway identity files (device keys).
    /// <c>tray.testhook.pairing.reset</c> wipes the contents to force re-pairing.
    /// Typically <c>%LOCALAPPDATA%/OpenClawTray</c> in production, or the
    /// isolated test dir in CI.
    /// </summary>
    string? LocalAppDataDir { get; }

    /// <summary>
    /// Invoke the SAME local-setup engine the LocalSetupProgressPage
    /// "Set up locally" button invokes. Returns a handle the hook can
    /// poll for status and cancel. Returns null if the host cannot create
    /// the engine (e.g. ConnectionManager not yet initialized).
    /// </summary>
    /// <param name="replaceExistingConfigurationConfirmed">
    /// Forwarded to <c>App.CreateLocalGatewaySetupEngine</c> — when true,
    /// allows the engine to clobber an existing setup state.
    /// </param>
    ITestHookLocalSetupRun? StartLocalSetup(bool replaceExistingConfigurationConfirmed);
}

/// <summary>
/// Handle to an in-flight local-setup run. Lets the test hook expose
/// status / cancel without exposing the engine itself.
/// </summary>
internal interface ITestHookLocalSetupRun
{
    string RunId { get; }
    TestHookLocalSetupStatus GetStatus();
    void Cancel();
    Task Completion { get; }
}

/// <summary>
/// Pure-data status snapshot for <c>tray.testhook.localSetup.status</c>.
/// </summary>
internal sealed record TestHookLocalSetupStatus(
    string Phase,
    string Status,
    string? Message,
    bool IsTerminal,
    string? ErrorMessage);

#endif
