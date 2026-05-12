using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OpenClawTray.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private AppState? _state;
    private SettingsManager? _settings;
    private VoiceService? _voiceService;
    private Action<string>? _dispatch;
    private bool _suppressMcpToggle;
    private bool _suppressTtsProviderChange;

    // Sentinel rendered into the API key PasswordBox so the user can see
    // that a key is already saved without us ever surfacing the plaintext.
    // Saving the form treats this exact value as "keep current key".
    private const string SavedApiKeySentinel = "••••••••";

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    internal void Initialize(AppState? state, SettingsManager? settings, VoiceService? voiceService, Action<string>? dispatch)
    {
        _state = state;
        _settings = settings;
        _voiceService = voiceService;
        _dispatch = dispatch;
        HostnameText.Text = Environment.MachineName;

        BuildCapabilityToggles();
        UpdateMcpStatus();
        UpdateSttCard();
        UpdateTtsCard();
        UpdateNodeStatus();
    }

    private void BuildCapabilityToggles()
    {
        if (_settings == null) return;
        var settings = _settings;

        var capabilities = new (string Icon, string Label, bool Value, Action<bool> Setter)[]
        {
            ("🔌", "Node Mode", settings.EnableNodeMode, v => settings.EnableNodeMode = v),
            ("🌐", "Browser Control", settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v),
            ("📷", "Camera", settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v),
            ("🎨", "Canvas", settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v),
            ("🖥️", "Screen Capture", settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v),
            ("📍", "Location", settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v),
            ("🔊", "Text-to-Speech", settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v),
            ("🎤", "Speech-to-Text", settings.NodeSttEnabled, v => settings.NodeSttEnabled = v),
        };

        var items = new List<UIElement>();
        foreach (var (icon, label, value, setter) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"{icon}  {label}",
                IsOn = value,
                MinWidth = 200
            };
            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                settings.Save();
                _dispatch?.Invoke("settings-saved");
                UpdateSttCard();
                UpdateTtsCard();
                UpdateNodeStatus();
            };
            items.Add(toggle);
        }

        CapabilityRepeater.ItemsSource = items;
    }

    // ============================================================
    // Speech-to-Text settings card
    // ============================================================

    private void UpdateSttCard()
    {
        var enabled = _settings?.NodeSttEnabled == true;
        SttCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || _settings == null) return;

        UpdateSttEngineHint();
    }

    private void UpdateSttEngineHint()
    {
        // Whisper is the only engine. Surface model-readiness so the user
        // knows what (if anything) needs to happen before stt.* will work.
        //
        // Check the file directly via WhisperModelManager rather than going
        // through VoiceService — that instance is only created
        // by NodeService.RegisterCapabilities() at Connect time, so a user
        // who toggled STT on but hasn't reconnected yet would see a stale
        // "not downloaded" message even with the file on disk.
        var modelName = _settings?.SttModelName ?? "base";
        var modelManager = new OpenClaw.Shared.Audio.WhisperModelManager(
            SettingsManager.SettingsDirectoryPath, new AppLogger());
        var modelDownloaded = modelManager.IsModelDownloaded(modelName);
        var modelDownloading = _voiceService?.IsWhisperDownloadingModel ?? false;

        if (modelDownloaded)
        {
            SttEngineHint.Text = "Whisper model is ready. Speech-to-text runs fully on this PC; no audio leaves the device.";
        }
        else if (modelDownloading)
        {
            SttEngineHint.Text = "Whisper model is downloading. Speech-to-text will be available once it's ready.";
        }
        else
        {
            SttEngineHint.Text = "Whisper model is not downloaded. Open More voice settings… to download it before using speech-to-text.";
        }
    }

    private void OnSttMoreSettingsClick(object sender, RoutedEventArgs e)
    {
        _dispatch?.Invoke("voice");
    }

    // ============================================================
    // Text-to-Speech settings card
    // ============================================================

    private void UpdateTtsCard()
    {
        var enabled = _settings?.NodeTtsEnabled == true;
        TtsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || _settings == null) return;

        var settings = _settings;

        _suppressTtsProviderChange = true;
        // ComboBox order: 0=Piper, 1=Windows, 2=ElevenLabs.
        TtsProviderComboBox.SelectedIndex = settings.TtsProvider switch
        {
            var p when string.Equals(p, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase) => 2,
            var p when string.Equals(p, TtsCapability.WindowsProvider, StringComparison.OrdinalIgnoreCase)    => 1,
            _ => 0  // default to Piper for unknown / null / whitespace
        };
        _suppressTtsProviderChange = false;

        // PasswordBox shows a masked sentinel when we already have a saved
        // key, so the user can tell something is set without us ever
        // putting plaintext on screen.
        TtsElevenLabsApiKeyBox.Password =
            string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
        TtsElevenLabsVoiceIdBox.Text = settings.TtsElevenLabsVoiceId;
        TtsElevenLabsModelBox.Text = settings.TtsElevenLabsModel;

        UpdateTtsElevenLabsPanelVisibility();
        TtsStatusText.Text = "";
    }

    private void UpdateTtsElevenLabsPanelVisibility()
    {
        var isEleven = (TtsProviderComboBox.SelectedItem is ComboBoxItem item)
            && string.Equals(item.Tag as string, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase);
        TtsElevenLabsPanel.Visibility = isEleven ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTtsProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTtsProviderChange) return;
        if (_settings == null) return;

        var newProvider = (TtsProviderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ? tag
            : TtsCapability.WindowsProvider;

        if (!string.Equals(_settings.TtsProvider, newProvider, StringComparison.OrdinalIgnoreCase))
        {
            _settings.TtsProvider = newProvider;
            _settings.Save();
            _dispatch?.Invoke("settings-saved");
            TtsStatusText.Text = $"Default provider: {newProvider}";
        }

        UpdateTtsElevenLabsPanelVisibility();
    }

    private void OnTtsElevenLabsCommitted(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        var settings = _settings;

        var changed = false;

        // Treat the sentinel as "keep existing"; only overwrite when the
        // user has typed a real key.
        var typedKey = TtsElevenLabsApiKeyBox.Password ?? "";
        if (!string.Equals(typedKey, SavedApiKeySentinel, StringComparison.Ordinal))
        {
            var trimmedKey = typedKey.Trim();
            if (!string.Equals(settings.TtsElevenLabsApiKey, trimmedKey, StringComparison.Ordinal))
            {
                settings.TtsElevenLabsApiKey = trimmedKey;
                changed = true;
            }
        }

        var voiceId = TtsElevenLabsVoiceIdBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsVoiceId, voiceId, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsVoiceId = voiceId;
            changed = true;
        }

        var model = TtsElevenLabsModelBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsModel, model, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsModel = model;
            changed = true;
        }

        if (changed)
        {
            settings.Save();
            _dispatch?.Invoke("settings-saved");
            // Re-render the API key field so the sentinel tracks the newly
            // saved state instead of leaving the typed key visible.
            TtsElevenLabsApiKeyBox.Password =
                string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
            TtsStatusText.Text = "ElevenLabs settings saved.";
        }
    }

    private void UpdateNodeStatus()
    {
        var nodeEnabled = _settings?.EnableNodeMode ?? false;
        var isConnected = (_state?.Status ?? ConnectionStatus.Disconnected) == ConnectionStatus.Connected;

        if (!nodeEnabled)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            NodeStatusText.Text = "Node mode disabled";
            NodeDetailsText.Text = "Enable Node Mode to provide device capabilities to agents.";
        }
        else if (isConnected)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = "Node active";

            var caps = new List<string>();
            if (_settings?.NodeBrowserProxyEnabled == true) caps.Add("browser");
            if (_settings?.NodeCameraEnabled == true) caps.Add("camera");
            if (_settings?.NodeCanvasEnabled == true) caps.Add("canvas");
            if (_settings?.NodeScreenEnabled == true) caps.Add("screen");
            if (_settings?.NodeLocationEnabled == true) caps.Add("location");
            if (_settings?.NodeTtsEnabled == true) caps.Add("tts");
            if (_settings?.NodeSttEnabled == true) caps.Add("stt");
            NodeDetailsText.Text = caps.Count > 0
                ? $"Providing {caps.Count} capabilities: {string.Join(", ", caps)}"
                : "No capabilities enabled.";
        }
        else
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = "Node mode enabled, not connected";
            NodeDetailsText.Text = "Connect to a gateway to start providing device capabilities.";
        }
    }

    private void UpdateMcpStatus()
    {
        var settings = _settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = System.IO.File.Exists(tokenPath);
            McpStatusText.Text = tokenExists ? "Server enabled — token ready" : "Server enabled — token will be created on next start";
        }
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle) return;
        if (_settings == null) return;
        _settings.EnableMcpServer = McpToggle.IsOn;
        _settings.Save();
        _dispatch?.Invoke("settings-saved");
        UpdateMcpStatus();
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (System.IO.File.Exists(tokenPath))
            {
                var token = System.IO.File.ReadAllText(tokenPath).Trim();
                var dp = new DataPackage();
                dp.SetText(token);
                Clipboard.SetContent(dp);
                McpStatusText.Text = "Token copied to clipboard";
            }
            else
            {
                McpStatusText.Text = "Token file not found — start the MCP server first";
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Failed to read token: {ex.Message}";
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(NodeService.McpServerUrl);
        Clipboard.SetContent(dp);
        McpStatusText.Text = "URL copied to clipboard";
    }
}