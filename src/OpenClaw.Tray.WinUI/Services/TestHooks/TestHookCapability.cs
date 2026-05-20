#if OPENCLAW_E2E_HOOKS

// ============================================================================
//  Gateway-compat E2E test hooks.
//
//  This file is compiled ONLY when MSBuild property OpenClawEnableTestHooks
//  is true (see OpenClaw.Tray.WinUI.csproj). It registers a `tray.testhook.*`
//  MCP tool surface on McpToolBridge that lets the gateway-compat harness
//  drive local-setup, gateway config, pairing reset, etc. without UI.
//
//  Production binaries MUST NOT define OPENCLAW_E2E_HOOKS. The
//  Release-build smoke test in OpenClaw.Tray.Tests
//  (ReleaseBuildExcludesTestHooksTests) asserts this type is absent from
//  the shipped assembly.
//
//  The tools themselves are placeholders right now (workstream W3
//  scaffolding); subsequent commits in W4 add the real implementations.
// ============================================================================

using System.Threading.Tasks;

namespace OpenClawTray.Services.TestHooks;

/// <summary>
/// Placeholder for the W3.x test-hook capability. Real implementation lands
/// in a follow-up commit that wires Mcp tool registrations via McpToolBridge.
/// </summary>
internal sealed class TestHookCapability
{
    public const string TestHookEnabledEnvironmentVariable = "OPENCLAW_TRAY_E2E";

    public static bool IsEnabledAtRuntime() =>
        System.Environment.GetEnvironmentVariable(TestHookEnabledEnvironmentVariable) == "1";

    public Task<string> PingAsync() => Task.FromResult("test-hooks-available");
}

#endif
