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
    private Mutex? _portableInstance;

    internal BridgeAppServices Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (PortablePreview.Enabled)
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                _portableInstance = new Mutex(false, @"Local\DeepOriBridge.Portable.v1." + sid);
                bool acquired;
                try { acquired = _portableInstance.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    _portableInstance.Dispose();
                    _portableInstance = null;
                    throw new InvalidOperationException("另一个便携版正在运行，请先正常退出旧版本。");
                }
                string hash = await Task.Run(PortablePreview.LockAndVerify);
                var root = new Microsoft.UI.Xaml.Controls.Grid();
                _window = new Window { Title = "DeepOri Bridge — 未签名便携测试版", Content = root };
                var loaded = new TaskCompletionSource();
                root.Loaded += (_, _) => loaded.TrySetResult();
                _window.Activate();
                await loaded.Task;
                if (!PortablePreview.HasConsent(hash))
                {
                    var consent = new Microsoft.UI.Xaml.Controls.ContentDialog
                    {
                        XamlRoot = root.XamlRoot,
                        Title = "信任此未签名测试版？",
                        Content = "此版本没有 Windows 发布者签名。请确认文件来自你信任的 GitHub Release。\n\n"
                            + "文件校验不能证明发布者身份，也不能抵御已控制同一 Windows 账户的攻击者。"
                            + "凭证与开发版、正式版隔离；版本或目录改变后需要重新确认。\n\n"
                            + "本次文件指纹：" + hash,
                        PrimaryButtonText = "我信任此版本并继续",
                        CloseButtonText = "退出",
                        DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
                    };
                    if (await consent.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    { _window.Close(); Exit(); return; }
                    PortablePreview.Accept(hash, persist: true);
                }
                else PortablePreview.Accept(hash, persist: false);
                Window consentWindow = _window;
                Services = new BridgeAppServices();
                _window = new MainWindow();
                _window.Activate();
                consentWindow.Close();
                BridgeCrashRecovery.Start();
                return;
            }
            Services = new BridgeAppServices();
            _window = new MainWindow();
            BridgeCrashRecovery.Start();
        }
        catch (Exception exception)
        {
            if (Services is not null)
            {
                try { await Services.ShutdownAsync(); } catch { }
            }
            if (PortablePreview.Enabled)
            {
                try
                {
                    Directory.CreateDirectory(PortablePreview.DataRoot);
                    if (FileAttestation.HasReparsePoint(PortablePreview.DataRoot)) throw new IOException("Unsafe diagnostic directory.");
                    File.WriteAllText(Path.Combine(PortablePreview.DataRoot, "startup-error.txt"),
                        exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
                }
                catch { /* Startup remains fail-closed even if diagnostics cannot be saved. */ }
            }
            Window? previousWindow = _window;
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
            _window.Closed += (_, _) => Exit();
            _window.Activate();
            previousWindow?.Close();
        }
        _window.Activate();
    }
}
