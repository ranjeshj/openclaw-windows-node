using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.ChatControl;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using System;
using System.Runtime.InteropServices;
using WinUIEx;

namespace OpenClawTray.Windows;

/// <summary>
/// Native WinUI chat window with Win32 tray integration:
/// tool window (hidden from taskbar), DPI-aware tray positioning,
/// auto-hide on deactivation, hide-instead-of-close.
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int PanelWidthDip = 480;
    private const int PanelHeightDip = 640;

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

        this.SetWindowSize(PanelWidthDip, PanelHeightDip);
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

        int panelWPx = (int)(PanelWidthDip * scale);
        int panelHPx = (int)(PanelHeightDip * scale);

        int margin = 8;
        int x = work.Right - panelWPx - margin;
        int y = work.Bottom - panelHPx - margin;

        this.Move(x, y);
        this.SetWindowSize(PanelWidthDip, PanelHeightDip);
        this.Show();
        SetForegroundWindow(hwnd);
    }

    private void OnChatCloseRequested(object sender, EventArgs e)
    {
        this.Hide();
    }

    public void ForceClose()
    {
        Closed -= OnWindowClosing;
        IsClosed = true;
        _viewModel?.Dispose();
        _gatewayChatService?.Dispose();
        Close();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            this.Hide();
    }

    private void OnWindowClosing(object sender, WindowEventArgs args)
    {
        args.Handled = true;
        this.Hide();
    }
}
