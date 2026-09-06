using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeepOriBridge;

public sealed partial class DiagnosticsPage : Page
{
    private readonly CodexEnvironmentProbe _probe;
    private readonly BridgeProductIdentity _productIdentity;
    private readonly FeishuEventRuntime _feishuEvents;
    private readonly PageIdentityCommandGate _pageIdentityGate = new();
    private CancellationTokenSource? _cancellation;

    public DiagnosticsPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _probe = services.EnvironmentProbe;
        _productIdentity = services.ProductIdentity;
        _feishuEvents = services.FeishuEvents;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        int bindingGeneration = _pageIdentityGate.BeginBinding();
        RefreshButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
                _productIdentity.BindPageContract("DeepOriBridge.Diagnostics.Page"));
        }
        catch (Exception exception)
        {
            _pageIdentityGate.TryMarkFaulted(bindingGeneration);
            RefreshButton.IsEnabled = false;
            ProductIdentityStatus.Title = "Bridge 页面身份核验失败";
            ProductIdentityStatus.Message = $"无法从后台观察线程绑定当前诊断页：{exception.Message}。诊断探测保持关闭。";
            ProductIdentityStatus.Severity = InfoBarSeverity.Error;
            return;
        }
        if (!_pageIdentityGate.TryMarkBound(bindingGeneration))
        {
            return;
        }

        ProductIdentityStatus.Title = _productIdentity.IsReleaseIdentityReady
            ? "Package 发布身份已认证"
            : "Windows 包身份仍是开发 Gate";
        ProductIdentityStatus.Message = _productIdentity.IsReleaseIdentityReady
            ? $"精确 package/签名/路径/进程/完整 payload 已认证；payload 指纹 {_productIdentity.PayloadManifestSha256[..12]}。"
            : $"当前为已认证固定 AppX 输出与完整 payload 的未签名本机预览版；Publisher 仍是 CN=AppPublisher，尚非正式签名发布。payload 指纹 {_productIdentity.PayloadManifestSha256[..12]}；只使用隔离预览 vault。官方 Bot 身份验证通过后，仅允许随包认证 sidecar 在精确 3/3 consumer ready 时进入 Listening。";
        ProductIdentityStatus.Severity = _productIdentity.IsReleaseIdentityReady
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        RenderFeishuConsumers();
        BackButton.Focus(FocusState.Programmatic);
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageIdentityGate.Invalidate();
        _cancellation?.Cancel();
        RefreshButton.IsEnabled = false;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await RefreshAsync();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => NavigateBack();

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            NavigateBack();
            e.Handled = true;
        }
    }

    private void NavigateBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(MainPage));
        }
    }

    private async Task RefreshAsync()
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        RefreshButton.IsEnabled = false;
        SummaryStatus.Title = "检查中";
        SummaryStatus.Message = "正在执行数据库文件头、Windows IPC 与 app-server 只读探测…";
        SummaryStatus.Severity = InfoBarSeverity.Informational;

        try
        {
            Render(await _probe.ProbeAsync(_cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            SummaryStatus.Title = "检查超时";
            SummaryStatus.Message = "Codex 接口没有在 15 秒内完成响应；没有修改任何本机状态。";
            SummaryStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            RefreshButton.IsEnabled = _pageIdentityGate.MayEnable(true);
        }
    }

    private void Render(CodexEnvironmentSnapshot snapshot)
    {
        RenderFeishuConsumers();
        CodexVersionText.Text = string.IsNullOrWhiteSpace(snapshot.CodexVersion) ? "✗ 未取得 Codex CLI 版本" : $"✓ {snapshot.CodexVersion}";
        CodexPathText.Text = snapshot.CodexExecutablePath;
        PrivatePipeStatusText.Text = snapshot.PrivatePipeDetected ? "✓ 检测到 Windows 命名管道 \\.\\pipe\\codex-ipc" : "○ 当前未检测到 \\.\\pipe\\codex-ipc";
        StateDatabaseStatusText.Text = snapshot.StateDatabaseIsValid ? "✓ 有效 SQLite 文件头" : "✗ 不存在或文件头不可读";
        StateDatabasePathText.Text = snapshot.StateDatabasePath;
        CatalogDatabaseStatusText.Text = snapshot.CatalogDatabaseIsValid ? "✓ 有效 SQLite 文件头" : "✗ 不存在或文件头不可读";
        CatalogDatabasePathText.Text = snapshot.CatalogDatabasePath;
        AppServerStatusText.Text = snapshot.AppServerInitialized ? $"✓ initialize 成功 · {snapshot.AppServerPlatform}" : "✗ initialize 未成功";
        ThreadListStatusText.Text = snapshot.ThreadListSucceeded ? $"✓ thread/list 只读请求成功（本页 {snapshot.ReturnedThreadCount} 条）" : "✗ thread/list 尚未验证";
        CheckedAtText.Text = $"最近检查：{snapshot.CheckedAt:yyyy-MM-dd HH:mm:ss}";

        bool ready = snapshot.StateDatabaseIsValid && snapshot.CatalogDatabaseIsValid
            && snapshot.AppServerInitialized && snapshot.ThreadListSucceeded
            && snapshot.AppServerPlatform.Equals("windows/windows", StringComparison.OrdinalIgnoreCase);
        SummaryStatus.Title = ready ? "Windows Codex 接口已就绪" : "存在未通过项";
        SummaryStatus.Message = ready ? "已核验真实数据库路径、Windows app-server 与 IPC 探测结果。" : snapshot.Error ?? "请查看下方逐项结果。";
        SummaryStatus.Severity = ready ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
    }

    private void RenderFeishuConsumers()
    {
        FeishuEventRuntimeSnapshot snapshot = _feishuEvents.Snapshot;
        FeishuConsumerStatus.Message =
            $"已认证注册 {snapshot.RegisteredConsumerCount}/{snapshot.ExpectedConsumerCount}；"
            + $"回调计数（消息/菜单/卡片）{snapshot.ReceivedEventCount}/{snapshot.MenuEventCount}/{snapshot.CardActionEventCount}。";
        FeishuConsumerStatus.Severity = snapshot.RegisteredConsumerCount == snapshot.ExpectedConsumerCount
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Informational;
    }
}
