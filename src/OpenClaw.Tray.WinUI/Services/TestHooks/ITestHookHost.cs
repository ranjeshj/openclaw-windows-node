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
    /// Typically <c>%LOCALAPPDATA%/OpenClawTray</c> in production, or the
    /// isolated test dir in CI. Currently unused (was for pairing.reset
    /// which has been removed) but kept so future hooks that need a
    /// data-dir can land without revisiting ITestHookHost.
    /// </summary>
    string? LocalAppDataDir { get; }
}

#endif
