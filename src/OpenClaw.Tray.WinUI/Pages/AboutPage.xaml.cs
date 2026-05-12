using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class AboutPage : Page
{
    private AppState? _state;
    private SettingsManager? _settings;
    private Action<string>? _dispatch;

    public AboutPage()
    {
        InitializeComponent();
    }

    internal void Initialize(AppState? state, SettingsManager? settings, Action<string>? dispatch)
    {
        _state = state;
        _settings = settings;
        _dispatch = dispatch;
        if (_state != null)
            _state.PropertyChanged += OnStateChanged;
        TryLoadGatewayInfo();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.GatewaySelf))
            RefreshGatewayInfo();
    }

    public void RefreshGatewayInfo() => TryLoadGatewayInfo();

    private void TryLoadGatewayInfo()
    {
        var self = _state?.GatewaySelf;
        if ((_state?.Status ?? ConnectionStatus.Disconnected) == ConnectionStatus.Connected && self != null)
        {
            GatewayVersionText.Text = self.VersionText;
            GatewayModelText.Text = self.Protocol.HasValue ? $"protocol v{self.Protocol}" : "unknown";
            GatewayAuthText.Text = string.IsNullOrWhiteSpace(self.AuthMode) ? "unknown" : self.AuthMode;
            GatewayUptimeText.Text = self.UptimeText;
        }
        else
        {
            GatewayVersionText.Text = "—";
            GatewayModelText.Text = "—";
            GatewayAuthText.Text = "—";
            GatewayUptimeText.Text = "—";
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "openclaw-tray.log");
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open log file: {ex.Message}");
        }
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray");
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open config folder: {ex.Message}");
        }
    }

    private async void OnCopySupportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var context = $"OpenClaw Hub v0.1.0\n"
                + $"OS: {Environment.OSVersion}\n"
                + $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n"
                + $"Connection: {_state?.Status ?? ConnectionStatus.Disconnected}\n"
                + $"Gateway: {_settings?.GetEffectiveGatewayUrl() ?? "n/a"}\n";

            var dataPackage = new WinDataTransfer.DataPackage();
            dataPackage.SetText(context);
            WinDataTransfer.Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to copy support context: {ex.Message}");
        }
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        _dispatch?.Invoke("checkupdates");
    }

    private void OnDocumentationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://openclaw.ai/docs") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open docs: {ex.Message}");
        }
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/openclaw/openclaw-windows-node") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open GitHub: {ex.Message}");
        }
    }

    private void OnDashboardClick(object sender, RoutedEventArgs e)
    {
        _dispatch?.Invoke("dashboard");
    }
}
