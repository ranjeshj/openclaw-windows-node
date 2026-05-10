using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Services;

/// <summary>
/// Handles copying diagnostic and support information to the clipboard.
/// Extracted from App.xaml.cs to reduce class size.
/// </summary>
internal sealed class DiagnosticsClipboardService
{
    private readonly Func<GatewayCommandCenterState> _stateFactory;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    /// <param name="stateFactory">
    /// Factory that builds a fresh <see cref="GatewayCommandCenterState"/> snapshot
    /// each time a copy operation is requested.
    /// </param>
    public DiagnosticsClipboardService(Func<GatewayCommandCenterState> stateFactory)
    {
        _stateFactory = stateFactory;
    }

    public void CopySupportContext()
        => CopyToClipboard("support context", s => CommandCenterTextHelper.BuildSupportContext(s));

    public void CopyDebugBundle()
        => CopyToClipboard("debug bundle", s => CommandCenterTextHelper.BuildDebugBundle(s));

    public void CopyBrowserSetupGuidance()
        => CopyToClipboard("browser setup guidance", s => CommandCenterTextHelper.BuildBrowserSetupGuidance(s));

    public void CopyPortDiagnostics()
        => CopyToClipboard("port diagnostics", s => CommandCenterTextHelper.BuildPortDiagnosticsSummary(s.PortDiagnostics));

    public void CopyCapabilityDiagnostics()
        => CopyToClipboard("capability diagnostics", s => CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary(s));

    public void CopyNodeInventory()
        => CopyToClipboard("node inventory", s => CommandCenterTextHelper.BuildNodeInventorySummary(s.Nodes));

    public void CopyChannelSummary()
        => CopyToClipboard("channel summary", s => CommandCenterTextHelper.BuildChannelSummaryText(s.Channels));

    public void CopyActivitySummary()
        => CopyToClipboard("activity summary", s => CommandCenterTextHelper.BuildActivitySummary(s.RecentActivity));

    public void CopyExtensibilitySummary()
        => CopyToClipboard("extensibility summary", s => CommandCenterTextHelper.BuildExtensibilitySummary(s.Channels));

    private void CopyToClipboard(string label, Func<GatewayCommandCenterState, string> textBuilder)
    {
        try
        {
            var state = _stateFactory();
            var package = new DataPackage();
            package.SetText(textBuilder(state));
            Clipboard.SetContent(package);
            Logger.Info($"Copied {label} from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy {label} from deep link: {ex.Message}");
        }
    }
}
