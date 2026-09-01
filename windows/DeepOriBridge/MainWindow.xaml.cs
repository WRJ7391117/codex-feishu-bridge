using Microsoft.UI.Xaml;

namespace DeepOriBridge;

/// <summary>
/// Hosts the Windows bridge status page.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
        AppWindow.Closing += OnAppWindowClosing;

        RootFrame.Navigate(typeof(MainPage));
    }

    private async void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            if (RootFrame.Content is MainPage page)
            {
                await page.ShutdownAsync();
            }
        }
        finally
        {
            _shutdownComplete = true;
            AppWindow.Closing -= OnAppWindowClosing;
            Close();
        }
    }
}
