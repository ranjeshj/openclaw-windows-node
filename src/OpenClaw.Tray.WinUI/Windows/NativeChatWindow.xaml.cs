using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.ChatControl;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Native WinUI chat window with Win32 tray integration:
/// tool window (hidden from taskbar), DPI-aware tray positioning,
/// auto-hide on deactivation, hide-instead-of-close.
/// Persists user-resized dimensions across sessions.
/// </summary>
public sealed partial class NativeChatWindow : Window
{
    private ChatViewModel? _viewModel;
    private GatewayChatService? _gatewayChatService;
    public bool IsClosed { get; private set; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT2 rect);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DefaultWidthDip = 480;
    private const int DefaultHeightDip = 640;

    private int _panelWidthDip = DefaultWidthDip;
    private int _panelHeightDip = DefaultHeightDip;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT2 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT2 rcMonitor;
        public RECT2 rcWork;
        public int dwFlags;
    }

    public NativeChatWindow()
    {
        InitializeComponent();

        LoadPersistedSize();

        this.SetWindowSize(_panelWidthDip, _panelHeightDip);
        this.SetIcon(Helpers.IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        // Hide system title bar and caption buttons (min/max/close)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));
        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Tool window: hidden from taskbar
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Rounded corners (Windows 11)
        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        Activated += OnWindowActivated;
        Closed += OnWindowClosing;
    }

    public void Initialize(IOperatorGatewayClient operatorClient)
    {
        _viewModel?.Dispose();
        _gatewayChatService?.Dispose();

        _gatewayChatService = new GatewayChatService(operatorClient);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _viewModel = new ChatViewModel(_gatewayChatService, action => dispatcher.TryEnqueue(() => action()));
        ChatControl.ViewModel = _viewModel;
        _ = _viewModel.LoadHistoryAsync();
    }

    public void ShowNearTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        GetCursorPos(out POINT pt);
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;

        int panelWPx = (int)(_panelWidthDip * scale);
        int panelHPx = (int)(_panelHeightDip * scale);

        int margin = 8;
        int x = work.Right - panelWPx - margin;
        int y = work.Bottom - panelHPx - margin;

        this.Move(x, y);
        this.SetWindowSize(_panelWidthDip, _panelHeightDip);
        this.Show();
        SetForegroundWindow(hwnd);
    }

    private void OnChatCloseRequested(object sender, EventArgs e)
    {
        SaveCurrentSize();
        this.Hide();
    }

    public void ForceClose()
    {
        SaveCurrentSize();
        Closed -= OnWindowClosing;
        IsClosed = true;
        _viewModel?.Dispose();
        _gatewayChatService?.Dispose();
        Close();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            SaveCurrentSize();
            this.Hide();
        }
    }

    private void OnWindowClosing(object sender, WindowEventArgs args)
    {
        args.Handled = true;
        SaveCurrentSize();
        this.Hide();
    }

    // ── Window size persistence ──

    private static string SizeFilePath =>
        Path.Combine(SettingsManager.SettingsDirectoryPath, "chat-window-state.json");

    private void LoadPersistedSize()
    {
        try
        {
            var path = SizeFilePath;
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
                _panelWidthDip = Math.Clamp(w.GetInt32(), 320, 1200);
            if (root.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                _panelHeightDip = Math.Clamp(h.GetInt32(), 400, 1600);
        }
        catch
        {
            // Ignore corrupt/missing file — use defaults
        }
    }

    private void SaveCurrentSize()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!GetWindowRect(hwnd, out var rect)) return;

            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi / 96.0;
            if (scale < 0.5) scale = 1.0;

            int widthDip = (int)((rect.Right - rect.Left) / scale);
            int heightDip = (int)((rect.Bottom - rect.Top) / scale);

            // Only persist reasonable sizes
            if (widthDip < 320 || widthDip > 1200 || heightDip < 400 || heightDip > 1600) return;

            _panelWidthDip = widthDip;
            _panelHeightDip = heightDip;

            var dir = Path.GetDirectoryName(SizeFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new { width = widthDip, height = heightDip });
            File.WriteAllText(SizeFilePath, json);
        }
        catch
        {
            // Best-effort — don't crash on save failure
        }
    }
}
