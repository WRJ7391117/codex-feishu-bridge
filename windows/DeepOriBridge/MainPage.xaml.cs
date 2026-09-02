using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepOriBridge;

public sealed partial class MainPage : Page
{
    private readonly CodexEnvironmentProbe _probe;
    private readonly BridgeRuntime _runtime;
    private readonly PromLightService _promLight;
    private readonly FeishuEventRuntime _feishuEvents;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _promLightCancellation;
    private bool _loaded;
    private bool _promLightReady;

    public MainPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _probe = services.EnvironmentProbe;
        _runtime = services.Runtime;
        _promLight = services.PromLight;
        _feishuEvents = services.FeishuEvents;
        RenderRuntime(_runtime.Snapshot);
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        SetPromLightCommandsEnabled(deviceReady: false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _runtime.StateChanged += OnRuntimeStateChanged;
        _feishuEvents.StateChanged += OnFeishuEventStateChanged;
        await RefreshEnvironmentAsync();
        await StartRuntimeAsync();
        await DetectPromLightAsync();

        // Activation can assign initial focus after Loaded returns. Restore the intended
        // landing position now and once more on the dispatcher after that focus pass.
        await Task.Delay(150);
        RestoreLandingPosition();
        DispatcherQueue.TryEnqueue(RestoreLandingPosition);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _runtime.StateChanged -= OnRuntimeStateChanged;
        _feishuEvents.StateChanged -= OnFeishuEventStateChanged;
        _operationCancellation?.Cancel();
        _promLightCancellation?.Cancel();
        await Task.CompletedTask;
    }

    private void RestoreLandingPosition()
    {
        RootScrollViewer.UpdateLayout();
        RefreshButton.Focus(FocusState.Programmatic);
        RootScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        await StartRuntimeAsync();
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        CancellationToken token = ReplaceOperationCancellation(TimeSpan.FromSeconds(5));
        try
        {
            await _runtime.StopAsync(token);
        }
        catch (OperationCanceledException)
        {
            OverallStatus.Title = "停止超时";
            OverallStatus.Message = "桥接会话仍在清理；关闭窗口时会再次执行清理。";
            OverallStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await RefreshEnvironmentAsync();
        if (_runtime.Snapshot.State == BridgeRuntimeState.Running)
        {
            CancellationToken token = ReplaceOperationCancellation(TimeSpan.FromSeconds(8));
            try
            {
                await _runtime.RefreshAsync(token);
            }
            catch (OperationCanceledException)
            {
                OverallStatus.Title = "会话刷新超时";
                OverallStatus.Message = "现有只读会话保持不变，可以稍后再次刷新。";
                OverallStatus.Severity = InfoBarSeverity.Warning;
            }
        }
    }

    private async void OnPromLightDetectClicked(object sender, RoutedEventArgs e)
    {
        await DetectPromLightAsync();
    }

    private async void OnPromLightIdleClicked(object sender, RoutedEventArgs e)
    {
        await SendPromLightAsync(PromLightStatus.Idle);
    }

    private async void OnPromLightRunningClicked(object sender, RoutedEventArgs e)
    {
        await SendPromLightAsync(PromLightStatus.Running);
    }

    private async void OnPromLightHumanGateClicked(object sender, RoutedEventArgs e)
    {
        await SendPromLightAsync(PromLightStatus.HumanGate);
    }

    private async void OnPromLightErrorClicked(object sender, RoutedEventArgs e)
    {
        await SendPromLightAsync(PromLightStatus.Error);
    }

    private void OnOpenSetupClicked(object sender, RoutedEventArgs e) =>
        NavigateTo(typeof(ConnectionSetupPage), "首次连接向导");

    private void OnOpenConfigurationClicked(object sender, RoutedEventArgs e) =>
        NavigateTo(typeof(BridgeConfigurationPage), "桥接配置");

    private void OnOpenDiagnosticsClicked(object sender, RoutedEventArgs e) =>
        NavigateTo(typeof(DiagnosticsPage), "运行诊断");

    private void NavigateTo(Type pageType, string pageName)
    {
        try
        {
            if (!Frame.Navigate(pageType))
            {
                throw new InvalidOperationException($"{pageName}页面未完成导航。");
            }
        }
        catch (Exception exception)
        {
            OverallStatus.Title = $"无法打开{pageName}";
            OverallStatus.Message = exception.Message;
            OverallStatus.Severity = InfoBarSeverity.Error;
        }
    }

    private async Task DetectPromLightAsync()
    {
        CancellationToken token = ReplacePromLightCancellation(TimeSpan.FromSeconds(10));
        _promLightReady = false;
        SetPromLightCommandsEnabled(deviceReady: false, busy: true);
        PromLightStatusText.Text = "正在通过 Windows HID 枚举 PromLight…";
        PromLightWriteText.Text = "检测过程只打开设备，不发送控制帧。";

        try
        {
            PromLightDeviceSnapshot snapshot = await _promLight.DiscoverAsync(token);
            bool supported = snapshot.Opened
                && snapshot.ReleaseNumber == PromLightService.SupportedReleaseNumber;
            _promLightReady = supported;
            SetPromLightCommandsEnabled(supported);

            if (supported)
            {
                PromLightStatusText.Text =
                    $"✓ 已打开 {snapshot.Product} {snapshot.DeviceVersion}（发现 {snapshot.DeviceCount} 台）";
                PromLightHardwareText.Text =
                    $"VID 07D7 · PID 6B01 · REV {snapshot.ReleaseNumber:X4} · Usage FF00:0001 · Report ID 2";
                PromLightWriteText.Text =
                    "已识别候选 HID 接口。提交报告只证明 Windows 写入成功；物理灯效仍需本次人工观察确认。";
            }
            else
            {
                PromLightStatusText.Text = snapshot.Found
                    ? "△ 已发现 PromLight，但尚未取得经过验证的读写接口"
                    : "○ 未发现可用的 PromLight HID 接口";
                PromLightHardwareText.Text = snapshot.ReleaseNumber == 0
                    ? "VID 07D7 · PID 6B01 · Usage FF00:0001"
                    : $"VID 07D7 · PID 6B01 · REV {snapshot.ReleaseNumber:X4} · Usage FF00:0001";
                PromLightWriteText.Text = snapshot.Error ?? "设备不可用。";
            }
        }
        catch (OperationCanceledException)
        {
            PromLightStatusText.Text = "△ PromLight 检测超时";
            PromLightWriteText.Text = "请确认蓝牙仍显示已连接，然后重新检测。";
            SetPromLightCommandsEnabled(deviceReady: false);
        }
    }

    private async Task SendPromLightAsync(PromLightStatus status)
    {
        CancellationToken token = ReplacePromLightCancellation(TimeSpan.FromSeconds(8));
        SetPromLightCommandsEnabled(_promLightReady, busy: true);
        PromLightWriteText.Text = $"正在提交“{PromLightStatusLabel(status)}”5E5E HID 报告…";

        try
        {
            PromLightWriteResult result = await _promLight.SendAsync(status, token);
            PromLightWriteText.Text =
                $"✓ Windows 已提交“{PromLightStatusLabel(result.Status)}”报告"
                + $"（{result.BytesWritten} 字节，{result.SubmittedAt:HH:mm:ss}）。"
                + "等待人工确认设备上的实际颜色与闪烁状态；本次写入不会自动标记物理灯效已验证。";
        }
        catch (OperationCanceledException)
        {
            PromLightWriteText.Text = "△ HID 写入超时，未确认提交结果。";
        }
        catch (Exception exception)
        {
            PromLightWriteText.Text = $"✗ HID 写入失败：{exception.Message}";
        }
        finally
        {
            SetPromLightCommandsEnabled(_promLightReady);
        }
    }

    private async Task StartRuntimeAsync()
    {
        CancellationToken token = ReplaceOperationCancellation(TimeSpan.FromSeconds(12));
        try
        {
            await _runtime.StartAsync(token);
        }
        catch (OperationCanceledException)
        {
            OverallStatus.Title = "会话启动超时";
            OverallStatus.Message = "Codex app-server 未在限定时间内完成初始化。";
            OverallStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private async Task RefreshEnvironmentAsync()
    {
        CancellationToken token = ReplaceOperationCancellation(TimeSpan.FromSeconds(12));
        SetCommandsEnabled(false);
        OverallStatus.Title = "正在检查";
        OverallStatus.Message = "正在执行本机只读探测与 app-server 初始化握手…";
        OverallStatus.Severity = InfoBarSeverity.Informational;

        try
        {
            CodexEnvironmentSnapshot snapshot = await _probe.ProbeAsync(token);
            RenderEnvironment(snapshot);
        }
        catch (OperationCanceledException)
        {
            OverallStatus.Title = "检查超时";
            OverallStatus.Message = "Codex 接口未在限定时间内响应；可以点击“刷新”。";
            OverallStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            RenderRuntime(_runtime.Snapshot);
        }
    }

    private void RenderEnvironment(CodexEnvironmentSnapshot snapshot)
    {
        CodexVersionText.Text = string.IsNullOrWhiteSpace(snapshot.CodexVersion)
            ? "✗ 未取得 Codex CLI 版本"
            : $"✓ {snapshot.CodexVersion}";
        LastCheckedText.Text = snapshot.CheckedAt.ToString("HH:mm:ss");

        bool ready = snapshot.StateDatabaseIsValid
            && snapshot.CatalogDatabaseIsValid
            && snapshot.AppServerInitialized
            && snapshot.AppServerPlatform.Equals("windows/windows", StringComparison.OrdinalIgnoreCase)
            && snapshot.ThreadListSucceeded;

        if (ready)
        {
            OverallStatus.Title = "Codex 已就绪";
            OverallStatus.Message = "已连接这台电脑上的 Codex Desktop；当前使用只读安全模式。";
            OverallStatus.Severity = InfoBarSeverity.Success;
        }
        else
        {
            OverallStatus.Title = "仍有检查项未通过";
            OverallStatus.Message = string.IsNullOrWhiteSpace(snapshot.Error)
                ? "请检查下方状态。"
                : snapshot.Error;
            OverallStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private void OnRuntimeStateChanged(BridgeRuntimeSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => RenderRuntime(snapshot));
    }

    private void RenderRuntime(BridgeRuntimeSnapshot snapshot)
    {
        RuntimeStatusText.Text = snapshot.State switch
        {
            BridgeRuntimeState.Starting => "DeepOri Bridge 正在启动",
            BridgeRuntimeState.Running => "DeepOri Bridge 已开启",
            BridgeRuntimeState.Stopping => "DeepOri Bridge 正在停止",
            BridgeRuntimeState.Faulted => "DeepOri Bridge 需要处理",
            _ => "DeepOri Bridge 已关闭",
        };

        RuntimeDetailText.Text = snapshot.State switch
        {
            BridgeRuntimeState.Running =>
                $"仅本次运行 · 已连接本机 Codex · 收到 {snapshot.NotificationCount} 个状态更新",
            BridgeRuntimeState.Faulted => snapshot.Error ?? "未知错误。",
            BridgeRuntimeState.Stopped => "飞书消息不会进入 Codex。",
            BridgeRuntimeState.Starting => "正在连接这台电脑上的 Codex…",
            _ => "正在安全关闭当前会话…",
        };

        bool transitioning = snapshot.State is BridgeRuntimeState.Starting or BridgeRuntimeState.Stopping;
        bool showStop = snapshot.State is BridgeRuntimeState.Running or BridgeRuntimeState.Stopping;
        StartButton.Visibility = showStop ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = showStop ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !transitioning && snapshot.State != BridgeRuntimeState.Running;
        StopButton.IsEnabled = !transitioning && snapshot.State == BridgeRuntimeState.Running;
        RefreshButton.IsEnabled = !transitioning;
    }

    private void SetCommandsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        RefreshButton.IsEnabled = enabled;
    }

    private CancellationToken ReplaceOperationCancellation(TimeSpan timeout)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource(timeout);
        return _operationCancellation.Token;
    }

    private CancellationToken ReplacePromLightCancellation(TimeSpan timeout)
    {
        _promLightCancellation?.Cancel();
        _promLightCancellation?.Dispose();
        _promLightCancellation = new CancellationTokenSource(timeout);
        return _promLightCancellation.Token;
    }

    private void SetPromLightCommandsEnabled(bool deviceReady, bool busy = false)
    {
        PromLightDetectButton.IsEnabled = !busy;
        PromLightIdleButton.IsEnabled = deviceReady && !busy;
        PromLightRunningButton.IsEnabled = deviceReady && !busy;
        PromLightHumanGateButton.IsEnabled = deviceReady && !busy;
        PromLightErrorButton.IsEnabled = deviceReady && !busy;
    }

    private void OnFeishuEventStateChanged(FeishuEventRuntimeSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => RenderFeishuEventRuntime(snapshot));
    }

    private void RenderFeishuEventRuntime(FeishuEventRuntimeSnapshot snapshot)
    {
        FeishuEventStatusText.Text = snapshot.State switch
        {
            FeishuEventRuntimeState.Configuring => "准备中",
            FeishuEventRuntimeState.Connecting => "连接中",
            FeishuEventRuntimeState.Listening => "已连接",
            FeishuEventRuntimeState.Stopping => "停止中",
            FeishuEventRuntimeState.Faulted => "连接失败",
            _ => "未连接",
        };

        FeishuEventCountText.Text = snapshot.ReceivedEventCount == 0
            ? "尚未收到测试消息；只统计事件数量，不保存消息正文与用户标识。"
            : $"已收到 {snapshot.ReceivedEventCount} 个消息事件"
                + (snapshot.LastReceivedAt is DateTimeOffset receivedAt
                    ? $"；最近时间 {receivedAt:HH:mm:ss}。"
                    : "。")
                + (snapshot.LastMatchDiagnostic is null
                    ? string.Empty
                    : $" {snapshot.LastMatchDiagnostic}");

        FeishuOwnerStatusText.Text = snapshot.OwnerBindingError is not null
            ? $"✗ {snapshot.OwnerBindingError}"
            : snapshot.OwnerBindingArmed
                ? $"一次性 owner 绑定码 {snapshot.OwnerBindingCode}（两分钟有效）。"
                : snapshot.OwnerBound
                    ? "✓ owner-only 白名单已绑定；sender ID 安全保存在 Windows Credential Locker。"
                    : "○ owner 尚未绑定；回执和 Codex 路由保持禁用。";

    }

    private static string PromLightStatusLabel(PromLightStatus status) => status switch
    {
        PromLightStatus.Idle => "空闲",
        PromLightStatus.Running => "运行中",
        PromLightStatus.HumanGate => "等待人工",
        PromLightStatus.Error => "错误",
        _ => status.ToString(),
    };

    private static string RuntimePlatformLabel(string? platform) =>
        string.IsNullOrWhiteSpace(platform)
            || platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase)
                ? "Windows"
                : platform;
}
