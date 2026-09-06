using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using DeepOriBridge.Services;

namespace DeepOriBridge;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    internal BridgeAppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = new BridgeAppServices();
            _window = new MainWindow();
            BridgeCrashRecovery.Start();
        }
        catch (Exception exception)
        {
            var message = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "安全启动已阻断：" + exception.Message,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetName(message, "DeepOri Bridge 安全启动阻断原因");
            var content = new Microsoft.UI.Xaml.Controls.Grid
            {
                Padding = new Thickness(32),
            };
            content.Children.Add(message);
            _window = new Window
            {
                Title = "DeepOri Bridge for Windows — 安全启动已阻断",
                Content = content,
            };
        }
        _window.Activate();
    }
}
