#if OPENCLAW_E2E_HOOKS

// ============================================================================
//  Gateway-compat E2E test-hook host wiring for App.
//
//  Compiled ONLY in E2E builds (-p:OpenClawEnableTestHooks=true). This is
//  the bridge between the App singleton (which owns the GatewayConnectionManager,
//  GatewayRegistry, ChatProvider, and LocalGatewaySetupEngine factory) and
//  the TestHookCapability MCP tool surface.
//
//  Production builds do NOT compile this file - enforced by
//  ReleaseBuildExcludesTestHooksTests.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClawTray.Services.TestHooks;

namespace OpenClawTray;

public partial class App : ITestHookHost
{
    // ITestHookHost surface. The first three forward to the same App
    // properties production UI uses, so the test hook can't drift from
    // what the user gets.
    IGatewayConnectionManager? ITestHookHost.ConnectionManager => _connectionManager;
    GatewayRegistry? ITestHookHost.Registry => _gatewayRegistry;
    OpenClawTray.Chat.OpenClawChatDataProvider? ITestHookHost.ChatProvider => _chatCoordinator?.Provider;
    string? ITestHookHost.LocalAppDataDir =>
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Same-path: invokes the same <see cref="CreateLocalGatewaySetupEngine"/>
    /// + <see cref="LocalGatewaySetupEngine.RunLocalOnlyAsync"/> chain that
    /// the production LocalSetupProgressPage / OnboardingV2Bridge invoke when
    /// the user clicks "Set up locally". Returns null if the App's
    /// dependencies are not yet initialized (e.g. the test hook fires before
    /// OnLaunched finishes).
    /// </summary>
    ITestHookLocalSetupRun? ITestHookHost.StartLocalSetup(bool replaceExistingConfigurationConfirmed)
    {
        LocalGatewaySetupEngine engine;
        try
        {
            engine = CreateLocalGatewaySetupEngine(replaceExistingConfigurationConfirmed);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new TestHookLocalSetupRun(engine);
    }

    private sealed class TestHookLocalSetupRun : ITestHookLocalSetupRun
    {
        private readonly LocalGatewaySetupEngine _engine;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task<LocalGatewaySetupState> _runTask;
        private volatile LocalGatewaySetupState? _latestState;
        private volatile string? _faultMessage;

        public string RunId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        public TestHookLocalSetupRun(LocalGatewaySetupEngine engine)
        {
            _engine = engine;
            // Track the latest state via the same StateChanged event the V2
            // bridge subscribes to (no parallel state machine).
            _engine.StateChanged += s => _latestState = s;
            // Kick off the same RunLocalOnlyAsync method LocalSetupProgressPage
            // invokes, with our own cancellation token so localSetup.cancel
            // can interrupt it.
            _runTask = Task.Run(async () =>
            {
                try
                {
                    return await _engine.RunLocalOnlyAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _faultMessage = ex.Message;
                    throw;
                }
            });
        }

        public TestHookLocalSetupStatus GetStatus()
        {
            var state = _latestState;
            if (state is null)
            {
                return new TestHookLocalSetupStatus(
                    Phase: "Pending",
                    Status: "Pending",
                    Message: "Setup engine has not yet emitted any state",
                    IsTerminal: false,
                    ErrorMessage: _faultMessage);
            }
            var isTerminal = state.Status is LocalGatewaySetupStatus.Complete
                or LocalGatewaySetupStatus.FailedTerminal
                or LocalGatewaySetupStatus.FailedRetryable
                or LocalGatewaySetupStatus.Cancelled;
            return new TestHookLocalSetupStatus(
                Phase: state.Phase.ToString(),
                Status: state.Status.ToString(),
                Message: state.UserMessage,
                IsTerminal: isTerminal,
                ErrorMessage: _faultMessage ?? state.UserMessage);
        }

        public void Cancel() => _cts.Cancel();

        public Task Completion => _runTask;
    }
}

#endif
