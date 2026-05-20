using System;
using System.Threading.Tasks;
using OpenClaw.Tray.IntegrationTests; // McpClient
using Xunit;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Collection-scoped fixture that drives the FULL production local-setup
/// flow once, then hands every gateway-tier scenario in the
/// <c>"Gateway"</c> collection a ready MCP client connected to a tray that
/// has:
/// <list type="bullet">
///   <item>installed the <c>OpenClawGateway</c> WSL distro,</item>
///   <item>installed the pinned (or override) openclaw gateway version,</item>
///   <item>completed operator + node pairing,</item>
///   <item>has fake-LLM running inside the distro,</item>
///   <item>has the fake-LLM provider patched into the gateway config.</item>
/// </list>
///
/// <para>
/// Plan A: the workflow no longer pre-installs WSL / openclaw. The whole
/// install is driven from this fixture using
/// <c>tray.testhook.localSetup.start</c> — the same path the
/// LocalSetupProgressPage "Set up locally" button hits. That is the exact
/// code we want to regression-test against new gateway versions.
/// </para>
///
/// <para>
/// Cost: ~3-4 minutes cold on windows-2025 (per W0 spike); amortized across
/// all scenarios in the collection. Use <see cref="GatewayCompatFixture"/>
/// (per-class) if a test needs a clean tray with no setup run, or
/// <see cref="ReconnectFixture"/> for tests that must own their own
/// pairing state.
/// </para>
/// </summary>
public sealed class GatewayCollectionFixture : IAsyncLifetime
{
    /// <summary>Underlying per-class fixture: spawns the E2E tray + MCP client.</summary>
    private readonly GatewayCompatFixture _tray = new();

    public McpClient Client => _tray.Client;
    public string DataDir => _tray.DataDir;

    /// <summary>Maximum wall time to allow localSetup.start to reach a terminal state.</summary>
    private static readonly TimeSpan LocalSetupTimeout = TimeSpan.FromMinutes(20);

    public async Task InitializeAsync()
    {
        await _tray.InitializeAsync().ConfigureAwait(false);
        await GatewayCompatScenarios.DriveLocalSetupAndPrepareGatewayAsync(
            Client, LocalSetupTimeout).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Free port 18789 so the NEXT fixture's localSetup install
            // doesn't fail with local_gateway_port_in_use. Best-effort.
            await GatewayCompatScenarios.TerminateDistroAsync().ConfigureAwait(false);
        }
        catch { /* swallow */ }
        await _tray.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// xUnit collection marker. Every scenario class that wants the shared
/// fully-installed-and-paired tray applies <c>[Collection("Gateway")]</c>.
/// xUnit then constructs one <see cref="GatewayCollectionFixture"/> and
/// reuses it across every test in the collection.
/// </summary>
[CollectionDefinition("Gateway")]
public sealed class GatewayCollection : ICollectionFixture<GatewayCollectionFixture>
{
}

/// <summary>
/// Per-class fixture for <see cref="ReconnectTests"/> — same end state as
/// the collection fixture, but isolated so the reset/re-pair dance doesn't
/// trash shared state for the rest of the gateway tier.
/// </summary>
public sealed class ReconnectFixture : IAsyncLifetime
{
    private readonly GatewayCompatFixture _tray = new();

    public McpClient Client => _tray.Client;
    public string DataDir => _tray.DataDir;

    public async Task InitializeAsync()
    {
        await _tray.InitializeAsync().ConfigureAwait(false);
        await GatewayCompatScenarios.DriveLocalSetupAndPrepareGatewayAsync(
            Client, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await GatewayCompatScenarios.TerminateDistroAsync().ConfigureAwait(false);
        }
        catch { /* swallow */ }
        await _tray.DisposeAsync().ConfigureAwait(false);
    }
}
