using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.ChatControl;

namespace OpenClaw.ChatControl.DevApp;

public sealed partial class MainWindow : Window
{
    private readonly MockChatService _mockService;
    private readonly ChatViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _mockService = new MockChatService();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _viewModel = new ChatViewModel(_mockService, action => dispatcher.TryEnqueue(() => action()));

        ChatControl.ViewModel = _viewModel;

        Title = "OpenClaw Chat Control — Dev";
        this.SetWindowSize(900, 700);
    }

    private async void OnLoadHistory(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadHistoryAsync();
    }

    private void OnShortResponse(object sender, RoutedEventArgs e)
    {
        _viewModel.SendCommand.Execute("Hello, how are you?");
    }

    private void OnMarkdownResponse(object sender, RoutedEventArgs e)
    {
        _viewModel.SendCommand.Execute("Show me some markdown formatting");
    }

    private void OnCodeResponse(object sender, RoutedEventArgs e)
    {
        _viewModel.SendCommand.Execute("Show me a code example");
    }

    private void OnLongResponse(object sender, RoutedEventArgs e)
    {
        _viewModel.SendCommand.Execute("Give me a long response");
    }

    private void OnSendError(object sender, RoutedEventArgs e)
    {
        _mockService.NextSendException = new System.InvalidOperationException("Simulated network error");
        _viewModel.SendCommand.Execute("This will fail");
    }

    private void OnStreamError(object sender, RoutedEventArgs e)
    {
        _mockService.NextStreamErrors = true;
        _viewModel.SendCommand.Execute("This will error mid-stream");
    }

    private void OnDelayChanged(object sender, RoutedEventArgs e) { }
}

// Extension to set window size without WinUIEx dependency
internal static class WindowExtensions
{
    public static void SetWindowSize(this Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }
}
