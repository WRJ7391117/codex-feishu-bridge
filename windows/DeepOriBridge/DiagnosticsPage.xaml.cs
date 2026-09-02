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
    private CancellationTokenSource? _cancellation;

    public DiagnosticsPage()
    {
        InitializeComponent();
        _probe = ((App)Application.Current).Services.EnvironmentProbe;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BackButton.Focus(FocusState.Programmatic);
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(CodexEnvironmentSnapshot snapshot)
    {
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
}
