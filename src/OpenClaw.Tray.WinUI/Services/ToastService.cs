using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Manages toast notification display with deduplication and sound settings.
/// Extracted from App.xaml.cs to isolate notification state and logic.
/// </summary>
internal sealed class ToastService
{
    private readonly SettingsManager? _settings;
    private readonly HashSet<string> _shownPairedToasts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _recentToastKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ToastDedupeWindow = TimeSpan.FromSeconds(30);

    public ToastService(SettingsManager? settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Shows a toast notification with optional deduplication by tag and device ID.
    /// Applies sound settings from the current <see cref="SettingsManager"/>.
    /// </summary>
    public void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null)
    {
        if (!ShouldShowToast(toastTag, deviceId))
            return;

        var sound = _settings?.NotificationSound;
        if (string.Equals(sound, "None", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new ToastAudio { Silent = true });
        }
        else if (string.Equals(sound, "Subtle", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new Uri("ms-winsoundevent:Notification.IM"), silent: false);
        }
        builder.Show();
    }

    /// <summary>
    /// Returns true if a toast with this tag and device has been shown within the dedupe window.
    /// Used to suppress closely-related toasts (e.g., "node-connected" right after "node-paired").
    /// </summary>
    public bool HasRecentToast(string toastTag, string? deviceId)
    {
        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        return _recentToastKeys.TryGetValue(BuildToastKey(toastTag, normalizedDeviceId), out var lastShown) &&
            DateTime.UtcNow - lastShown < ToastDedupeWindow;
    }

    /// <summary>
    /// Marks a "Node paired" toast as shown for the given device key.
    /// Returns true if this is the first time (toast should be shown), false if duplicate.
    /// </summary>
    public bool TryMarkPairedToastShown(string deviceKey)
        => _shownPairedToasts.Add(deviceKey);

    /// <summary>
    /// Returns the path to the notification icon for the given type, or null if not found.
    /// </summary>
    public static string? GetNotificationIcon(string? type)
    {
        var appDir = AppContext.BaseDirectory;
        var iconPath = System.IO.Path.Combine(appDir, "Assets", "claw.ico");
        return System.IO.File.Exists(iconPath) ? iconPath : null;
    }

    private bool ShouldShowToast(string? toastTag, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(toastTag))
            return true;

        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        var dedupeKey = BuildToastKey(toastTag, normalizedDeviceId);
        var now = DateTime.UtcNow;

        foreach (var staleKey in _recentToastKeys
            .Where(pair => now - pair.Value >= ToastDedupeWindow)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _recentToastKeys.Remove(staleKey);
        }

        if (_recentToastKeys.TryGetValue(dedupeKey, out var lastShown) &&
            now - lastShown < ToastDedupeWindow)
        {
            Logger.Info($"[ToastDeduper] Suppressed duplicate toast tag={toastTag} deviceId={normalizedDeviceId}");
            return false;
        }

        _recentToastKeys[dedupeKey] = now;
        Logger.Info($"[ToastDeduper] Showing toast tag={toastTag} deviceId={normalizedDeviceId}");
        return true;
    }

    private static string NormalizeToastDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? "global" : deviceId.Trim();

    private static string BuildToastKey(string toastTag, string normalizedDeviceId) =>
        $"{toastTag.Trim()}:{normalizedDeviceId}";
}
