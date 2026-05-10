using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Read-only snapshot of application state needed to build the tray menu popup.
/// </summary>
internal sealed class TrayMenuState
{
    public required ConnectionStatus Status { get; init; }
    public required GatewaySelfInfo? GatewaySelf { get; init; }
    public required SettingsManager? Settings { get; init; }
    public required string? AuthFailureMessage { get; init; }
    public required SessionInfo[] Sessions { get; init; }
    public required PairingListInfo? NodePairList { get; init; }
    public required DevicePairingListInfo? DevicePairList { get; init; }
    public required GatewayNodeInfo[] Nodes { get; init; }
    public required PresenceEntry[]? Presence { get; init; }
    public required string IdentityDataPath { get; init; }

    // Node service state
    public bool NodeServiceAvailable { get; init; }
    public bool NodeIsPaired { get; init; }
    public bool NodeIsPendingApproval { get; init; }
    public bool NodeIsConnected { get; init; }
}

/// <summary>
/// Callbacks for actions triggered by tray menu interactions.
/// </summary>
internal sealed class TrayMenuCallbacks
{
    public required Action OnConnect { get; init; }
    public required Action OnDisconnect { get; init; }
    public required Action<string> NavigateHub { get; init; }
    public required Action OnSettingsSaveAndReconnect { get; init; }
}

/// <summary>
/// Builds the system tray popup menu content. Extracted from App.xaml.cs to reduce
/// the size and responsibility of the App class.
/// </summary>
internal static class TrayMenuBuilder
{
    internal static readonly FrozenDictionary<string, string> CapabilityIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["screen"] = "🖥",
        ["camera"] = "📷",
        ["browser"] = "🌐",
        ["clipboard"] = "📋",
        ["tts"] = "🔊",
        ["location"] = "📍",
        ["canvas"] = "🎨",
        ["system"] = "⚙",
        ["device"] = "📱",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }

    internal static Grid BuildSectionHeader(string title, string summary)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(new TextBlock
        {
            Text = summary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    internal static UIElement BuildSessionCard(SessionInfo session)
    {
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? (int)(Math.Min(1.0, (double)usedTokens / contextTokens) * 100) : 0;
        var isActive = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase);
        var isIdle = string.Equals(session.Status, "idle", StringComparison.OrdinalIgnoreCase);

        var grid = new Grid
        {
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RowSpacing = 2,
            ColumnSpacing = 6
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // status dot
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // model badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron

        // Row 0: status dot
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isActive ? Microsoft.UI.Colors.LimeGreen
                : isIdle ? Microsoft.UI.Colors.Orange
                : Microsoft.UI.Colors.Gray)
        };
        Grid.SetRow(dot, 0);
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Row 0: session name
        var nameBlock = new TextBlock
        {
            Text = session.DisplayName ?? session.Key,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(nameBlock, 0);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        // Row 0: model badge
        if (!string.IsNullOrEmpty(session.Model))
        {
            var modelBadge = BuildBadge(session.Model);
            Grid.SetRow(modelBadge, 0);
            Grid.SetColumn(modelBadge, 2);
            grid.Children.Add(modelBadge);
        }

        // Row 0: chevron
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(chevron, 0);
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        // Row 1: token info + channel badge + status
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tokenText = usedTokens > 0
            ? $"{FormatTokenCount(usedTokens)}/{FormatTokenCount(contextTokens)} ({pct}%)"
            : "";
        if (!string.IsNullOrEmpty(tokenText))
        {
            row1.Children.Add(new TextBlock
            {
                Text = tokenText,
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = false
            });
        }
        if (!string.IsNullOrEmpty(session.Channel))
        {
            var channelAbbrev = session.Channel!.Length <= 2
                ? session.Channel.ToUpperInvariant()
                : session.Channel[..2].ToUpperInvariant();
            row1.Children.Add(BuildBadge(channelAbbrev));
        }
        var statusText = string.IsNullOrEmpty(session.Status) ? "Unknown"
            : char.ToUpperInvariant(session.Status[0]) + session.Status[1..];
        row1.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetRow(row1, 1);
        Grid.SetColumn(row1, 1);
        Grid.SetColumnSpan(row1, 3);
        grid.Children.Add(row1);

        // Row 2: thin progress bar
        if (usedTokens > 0)
        {
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = pct,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(1.5),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    pct > 80 ? Microsoft.UI.Colors.Red
                    : pct > 50 ? Microsoft.UI.Colors.Orange
                    : Microsoft.UI.Colors.LimeGreen)
            };
            Grid.SetRow(bar, 2);
            Grid.SetColumn(bar, 0);
            Grid.SetColumnSpan(bar, 4);
            grid.Children.Add(bar);
        }

        return grid;
    }

    internal static List<TrayMenuFlyoutItem> BuildSessionFlyoutItems(SessionInfo session)
    {
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? (int)(Math.Min(1.0, (double)usedTokens / contextTokens) * 100) : 0;
        var statusIcon = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase) ? "🟢"
            : string.Equals(session.Status, "done", StringComparison.OrdinalIgnoreCase) ? "✅" : "⚪";

        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = session.DisplayName ?? session.Key, IsHeader = true },
        };

        // Model · Provider
        var modelParts = new List<string>();
        if (!string.IsNullOrEmpty(session.Model)) modelParts.Add(session.Model);
        if (!string.IsNullOrEmpty(session.Provider)) modelParts.Add(session.Provider);
        if (modelParts.Count > 0) items.Add(new() { Text = string.Join(" · ", modelParts) });

        // Channel
        if (!string.IsNullOrEmpty(session.Channel))
            items.Add(new() { Text = $"📡 {session.Channel}" });

        // Status · age
        items.Add(new() { Text = $"{statusIcon} {session.Status} · {session.AgeText}" });

        // Token usage
        items.Add(new() { Text = "Token Usage", IsHeader = true });
        if (usedTokens > 0)
        {
            items.Add(new() { Text = $"Input     {FormatTokenCount(session.InputTokens)}" });
            items.Add(new() { Text = $"Output    {FormatTokenCount(session.OutputTokens)}" });
            items.Add(new() { Text = $"Total     {FormatTokenCount(usedTokens)} / {FormatTokenCount(contextTokens)} ({pct}%)" });
        }
        else
        {
            items.Add(new() { Text = "No token usage yet" });
        }

        // Context window
        if (session.ContextTokens > 0)
            items.Add(new() { Text = $"Context   {FormatTokenCount(session.ContextTokens)} window" });

        // Thinking / Verbose
        if (!string.IsNullOrEmpty(session.ThinkingLevel) || !string.IsNullOrEmpty(session.VerboseLevel))
        {
            items.Add(new() { Text = "Settings", IsHeader = true });
            if (!string.IsNullOrEmpty(session.ThinkingLevel))
                items.Add(new() { Text = $"🧠 Thinking: {session.ThinkingLevel}" });
            if (!string.IsNullOrEmpty(session.VerboseLevel))
                items.Add(new() { Text = $"📝 Verbose: {session.VerboseLevel}" });
        }

        // Subject / Room
        if (!string.IsNullOrEmpty(session.Subject))
            items.Add(new() { Text = $"Subject: {session.Subject}" });
        if (!string.IsNullOrEmpty(session.Room))
            items.Add(new() { Text = $"Room: {session.Room}" });

        return items;
    }

    internal static UIElement BuildDeviceCard(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;

        var grid = new Grid
        {
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RowSpacing = 2,
            ColumnSpacing = 6
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // dot
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // platform badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron

        // Row 0: status dot
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                node.IsOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray)
        };
        Grid.SetRow(dot, 0);
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Row 0: device name
        var nameBlock = new TextBlock
        {
            Text = nodeName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(nameBlock, 0);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        // Row 0: platform badge
        if (!string.IsNullOrEmpty(node.Platform))
        {
            var badge = BuildBadge(node.Platform);
            Grid.SetRow(badge, 0);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        // Row 0: chevron
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(chevron, 0);
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        // Row 1: capability icons + count + online/offline
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Capability emoji icons
        var capIcons = new System.Text.StringBuilder();
        if (node.Capabilities.Count > 0)
        {
            foreach (var cap in node.Capabilities)
            {
                if (CapabilityIcons.TryGetValue(cap, out var icon))
                    capIcons.Append(icon);
            }
        }
        var capText = capIcons.Length > 0
            ? $"{capIcons} {node.CapabilityCount} caps"
            : node.CapabilityCount > 0 ? $"{node.CapabilityCount} caps" : "";
        var statusLabel = node.IsOnline ? "online" : "offline";
        var row1Text = !string.IsNullOrEmpty(capText) ? $"{capText}  ·  {statusLabel}" : statusLabel;

        row1.Children.Add(new TextBlock
        {
            Text = row1Text,
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetRow(row1, 1);
        Grid.SetColumn(row1, 1);
        Grid.SetColumnSpan(row1, 3);
        grid.Children.Add(row1);

        return grid;
    }

    internal static List<TrayMenuFlyoutItem> BuildDeviceFlyoutItems(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = nodeName, IsHeader = true },
        };

        // Status + platform + mode on one line
        var statusIcon = node.IsOnline ? "🟢" : "⚪";
        var statusText = node.IsOnline ? "Online" : "Offline";
        var infoParts = new List<string> { $"{statusIcon} {statusText}" };
        if (!string.IsNullOrEmpty(node.Platform)) infoParts.Add(node.Platform);
        if (!string.IsNullOrEmpty(node.Mode)) infoParts.Add(node.Mode);
        items.Add(new() { Text = string.Join(" · ", infoParts) });

        // Last seen
        if (node.LastSeen.HasValue)
        {
            var age = DateTime.UtcNow - node.LastSeen.Value;
            var seenText = age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            items.Add(new() { Text = $"Last seen {seenText}" });
        }

        // Capabilities + Commands merged
        if (node.Capabilities.Count > 0 || node.Commands.Count > 0)
        {
            items.Add(new() { Text = $"Capabilities ({node.CapabilityCount}) · Commands ({node.CommandCount})", IsHeader = true });

            var cmdGroups = node.Commands
                .GroupBy(c => c.Contains('.') ? c[..c.IndexOf('.')] : c, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Contains('.') ? c[(c.IndexOf('.') + 1)..] : c).ToList(), StringComparer.OrdinalIgnoreCase);

            var shownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in node.Capabilities)
            {
                var icon = CapabilityIcons.TryGetValue(cap, out var emoji) ? emoji : "▪";
                if (cmdGroups.TryGetValue(cap, out var cmds) && cmds.Count > 0)
                {
                    items.Add(new() { Text = $"{icon} {cap}" });
                    items.Add(new() { Text = $"    {string.Join(", ", cmds)}" });
                    shownGroups.Add(cap);
                }
                else
                {
                    items.Add(new() { Text = $"{icon} {cap}" });
                    shownGroups.Add(cap);
                }
            }

            foreach (var group in cmdGroups.Where(g => !shownGroups.Contains(g.Key)).OrderBy(g => g.Key))
            {
                items.Add(new() { Text = $"▸ {group.Key}" });
                items.Add(new() { Text = $"    {string.Join(", ", group.Value)}" });
            }
        }

        return items;
    }

    internal static Border BuildBadge(string text)
    {
        return new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = false
            }
        };
    }

    /// <summary>
    /// Builds the full tray menu popup content from the given state snapshot and callbacks.
    /// </summary>
    internal static void Build(TrayMenuWindow menu, TrayMenuState state, TrayMenuCallbacks callbacks)
    {
        var isConnected = state.Status == ConnectionStatus.Connected;
        var statusText = LocalizationHelper.GetConnectionStatusText(state.Status);

        // ── Brand Header (non-interactive) ──
        menu.AddCustomElement(new StackPanel
        {
            Padding = new Thickness(14, 10, 14, 6),
            Children =
            {
                new TextBlock
                {
                    Text = "🦞 OpenClaw",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 14
                }
            }
        });

        // ── Gateway Section ──
        var gwGrid = new Grid
        {
            Padding = new Thickness(14, 4, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var gwInfo = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        // Gateway status line
        var gwStatusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        gwStatusRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isConnected ? Microsoft.UI.Colors.LimeGreen
                : state.Status == ConnectionStatus.Connecting ? Microsoft.UI.Colors.Orange
                : Microsoft.UI.Colors.Gray)
        });
        gwStatusRow.Children.Add(new TextBlock
        {
            Text = $"Gateway · {statusText}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        gwInfo.Children.Add(gwStatusRow);

        // Gateway details
        if (isConnected)
        {
            var detailParts = new List<string>();
            if (state.GatewaySelf != null && !string.IsNullOrEmpty(state.GatewaySelf.ServerVersion))
                detailParts.Add($"v{state.GatewaySelf.ServerVersion}");
            var url = state.Settings?.GetEffectiveGatewayUrl();
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                detailParts.Add($"{uri.Host}:{uri.Port}");
            if (state.Presence != null && state.Presence.Length > 0)
                detailParts.Add($"{state.Presence.Length} client{(state.Presence.Length != 1 ? "s" : "")}");
            if (detailParts.Count > 0)
            {
                gwInfo.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", detailParts),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 11
                });
            }
        }

        // Node pairing status
        if (state.Settings?.EnableNodeMode == true && state.NodeServiceAvailable)
        {
            var nodeText = state.NodeIsPaired ? "Node paired"
                : state.NodeIsPendingApproval ? "⏳ Node pairing pending"
                : state.NodeIsConnected ? "Node connected"
                : null;
            if (nodeText != null)
            {
                gwInfo.Children.Add(new TextBlock
                {
                    Text = nodeText,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 11
                });
            }
        }

        // Auth failure
        if (!string.IsNullOrEmpty(state.AuthFailureMessage))
        {
            gwInfo.Children.Add(new TextBlock
            {
                Text = $"⚠️ {state.AuthFailureMessage}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240
            });
        }

        Grid.SetColumn(gwInfo, 0);
        gwGrid.Children.Add(gwInfo);

        // Gateway connect/disconnect button
        var connectBtn = new ToggleButton
        {
            IsChecked = isConnected,
            Content = isConnected ? "Connected" : "Disconnected",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 4, 10, 4),
            MinHeight = 0,
            MinWidth = 0,
            FontSize = 11
        };
        ToolTipService.SetToolTip(connectBtn, isConnected ? "Click to disconnect from gateway" : "Click to connect to gateway");
        connectBtn.Click += (s, ev) =>
        {
            var on = connectBtn.IsChecked == true;
            connectBtn.Content = on ? "Connected" : "Disconnected";
            ToolTipService.SetToolTip(connectBtn, on ? "Click to disconnect from gateway" : "Click to connect to gateway");
            if (on)
                callbacks.OnConnect();
            else
                callbacks.OnDisconnect();
            menu.HideCascade();
        };
        Grid.SetColumn(connectBtn, 1);
        gwGrid.Children.Add(connectBtn);

        // Make gateway info area clickable → opens Connection page
        gwInfo.Tapped += (s, ev) =>
        {
            callbacks.NavigateHub("connection");
        };
        menu.AddCustomElement(gwGrid);

        // ── Sessions ──
        if (state.Sessions.Length > 0)
        {
            menu.AddSeparator();

            var sessionCount = state.Sessions.Length;
            var activeCount = state.Sessions.Count(s => string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase));
            var totalTokensAll = state.Sessions.Sum(s => s.InputTokens + s.OutputTokens);
            var sessionSummaryRight = $"{activeCount} active · {FormatTokenCount(totalTokensAll)} tokens";
            menu.AddCustomElement(BuildSectionHeader("Sessions", sessionSummaryRight));

            foreach (var session in state.Sessions.Take(5))
            {
                var card = BuildSessionCard(session);
                var flyoutItems = BuildSessionFlyoutItems(session);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "sessions");
            }
        }

        // ── Pairing Pending ──
        var nodePendingCount = state.NodePairList?.Pending.Count ?? 0;
        var devicePendingCount = state.DevicePairList?.Pending.Count ?? 0;
        if (nodePendingCount + devicePendingCount > 0)
        {
            var total = nodePendingCount + devicePendingCount;
            menu.AddMenuItem($"⚠️ Pairing approval pending ({total})", "🔗", "hub");
        }

        // ── Connected Devices with inline permission toggles ──
        if (state.Nodes.Length > 0)
        {
            menu.AddSeparator();

            var onlineCount = state.Nodes.Count(n => n.IsOnline);
            var totalCaps = state.Nodes.Sum(n => n.CapabilityCount);
            var deviceSummaryRight = $"{onlineCount} online · {totalCaps} caps";
            menu.AddCustomElement(BuildSectionHeader("Devices", deviceSummaryRight));

            var currentHost = Environment.MachineName;

            foreach (var node in state.Nodes.Take(5))
            {
                var card = BuildDeviceCard(node);
                var flyoutItems = BuildDeviceFlyoutItems(node);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "nodes");

                // If this node is the local machine, show capability toggles underneath
                bool isLocal = node.DisplayName?.Contains(currentHost, StringComparison.OrdinalIgnoreCase) == true
                    || node.NodeId?.Contains(currentHost, StringComparison.OrdinalIgnoreCase) == true;
                if (isLocal && state.Settings != null)
                {
                    var settings = state.Settings;
                    var capToggles = new Dictionary<string, (Func<bool> Get, Action<bool> Set)>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["browser"] = (() => settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v),
                        ["camera"] = (() => settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v),
                        ["canvas"] = (() => settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v),
                        ["screen"] = (() => settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v),
                        ["location"] = (() => settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v),
                        ["tts"] = (() => settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v),
                        ["system"] = (() => settings.EnableNodeMode, v => settings.EnableNodeMode = v),
                    };

                    var allCaps = capToggles.Keys.ToList();

                    if (allCaps.Count > 0)
                    {
                        var columns = 3;
                        var grid = new Grid
                        {
                            Margin = new Thickness(28, 4, 14, 4),
                            ColumnSpacing = 4,
                            RowSpacing = 4,
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        for (int c = 0; c < columns; c++)
                            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var rowCount = (allCaps.Count + columns - 1) / columns;
                        for (int r = 0; r < rowCount; r++)
                            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                        for (int i = 0; i < allCaps.Count; i++)
                        {
                            var cap = allCaps[i];
                            var capToggle = capToggles[cap];
                            var icon = CapabilityIcons.TryGetValue(cap, out var emoji) ? emoji : "▪";
                            var label = char.ToUpper(cap[0]) + cap[1..];
                            var isOn = capToggle.Get();

                            var btn = new ToggleButton
                            {
                                IsChecked = isOn,
                                HorizontalAlignment = HorizontalAlignment.Stretch,
                                HorizontalContentAlignment = HorizontalAlignment.Center,
                                Padding = new Thickness(6, 5, 6, 5),
                                MinHeight = 0,
                                MinWidth = 0,
                                Content = new TextBlock
                                {
                                    Text = $"{icon} {label}",
                                    FontSize = 11,
                                    TextTrimming = TextTrimming.CharacterEllipsis
                                }
                            };
                            var capRef = capToggle;
                            btn.Click += (s, ev) =>
                            {
                                var on = ((ToggleButton)s!).IsChecked == true;
                                capRef.Set(on);
                                callbacks.OnSettingsSaveAndReconnect();
                            };
                            Grid.SetRow(btn, i / columns);
                            Grid.SetColumn(btn, i % columns);
                            grid.Children.Add(btn);
                        }
                        menu.AddCustomElement(grid);
                    }
                }
            }
        }

        // ── Actions ──
        menu.AddSeparator();
        menu.AddMenuItem("Dashboard", "🌐", "dashboard");
        menu.AddMenuItem("Chat", "💬", "openchat");
        menu.AddMenuItem("Canvas", "🎨", "canvas");
        menu.AddMenuItem("Voice", "🎙️", "voice");
        menu.AddMenuItem("Companion", "🦞", "companion");
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_QuickSend"), "📤", "quicksend");

        var setupMenuLabel = state.Settings != null
            && new OpenClawTray.Onboarding.Services.OnboardingExistingConfigGuard(state.Settings, state.IdentityDataPath)
                .HasExistingConfiguration()
            ? LocalizationHelper.GetString("Menu_Reconfigure")
            : LocalizationHelper.GetString("Menu_SetupGuide");
        menu.AddMenuItem(setupMenuLabel, "🧭", "setup");

        // ── Exit ──
        menu.AddSeparator();
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_Exit"), "❌", "exit");
    }
}
