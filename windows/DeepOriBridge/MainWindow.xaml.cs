using Microsoft.UI.Xaml;
using DeepOriBridge.Models;

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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
        AppWindow.Closing += OnAppWindowClosing;

        var services = ((App)Application.Current).Services;
        FeishuStoredCredential credential = services.FeishuCredentials.Load();
        bool setupComplete = credential.HasSecret
            && credential.HasVerifiedIdentity
            && services.FeishuCredentials.LoadOwnerBinding().IsBound
            && services.FeishuCredentials.LoadProjectAuthorization().IsAuthorized;
        RootFrame.Navigate(setupComplete ? typeof(MainPage) : typeof(ConnectionSetupPage));
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
            await ((App)Application.Current).Services.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;
            AppWindow.Closing -= OnAppWindowClosing;
            Close();
        }
    }
}
