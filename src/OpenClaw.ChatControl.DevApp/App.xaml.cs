using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace OpenClaw.ChatControl.DevApp;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
            $"UnhandledException: {e.Exception}\n\nMessage: {e.Message}");
    }
}
