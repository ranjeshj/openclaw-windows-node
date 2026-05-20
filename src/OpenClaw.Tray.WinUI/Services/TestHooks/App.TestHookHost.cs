#if OPENCLAW_E2E_HOOKS

// ============================================================================
//  Gateway-compat E2E test-hook host wiring for App.
//
//  Compiled ONLY in E2E builds (-p:OpenClawEnableTestHooks=true). This is
//  the bridge between the App singleton (which owns the GatewayConnectionManager,
//  GatewayRegistry, ChatProvider) and the TestHookCapability MCP tool surface.
//
//  Production builds do NOT compile this file - enforced by
//  ReleaseBuildExcludesTestHooksTests.
// ============================================================================

using System;
using OpenClaw.Connection;
using OpenClawTray.Services.TestHooks;

namespace OpenClawTray;

public partial class App : ITestHookHost
{
    // ITestHookHost surface. Forwards to the same App properties production
    // UI uses, so the test hook can't drift from what the user gets.
    IGatewayConnectionManager? ITestHookHost.ConnectionManager => _connectionManager;
    GatewayRegistry? ITestHookHost.Registry => _gatewayRegistry;
    OpenClawTray.Chat.OpenClawChatDataProvider? ITestHookHost.ChatProvider => _chatCoordinator?.Provider;
    string? ITestHookHost.LocalAppDataDir =>
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

#endif
