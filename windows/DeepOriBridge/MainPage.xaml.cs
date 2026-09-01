using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepOriBridge;

public sealed partial class MainPage : Page
{
    private readonly CodexEnvironmentProbe _probe = new();
    private readonly BridgeRuntime _runtime = new();
    private readonly PromLightService _promLight = new();
    private readonly FeishuCredentialService _feishuCredentials = new();
    private readonly FeishuEventRuntime _feishuEvents;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _promLightCancellation;
    private CancellationTokenSource? _feishuCancellation;
    private bool _loaded;
    private bool _promLightReady;
    private bool _codexRouteExecuting;
    private bool _readOnlyRouteExecuting;
    private Task? _shutdownTask;

    public MainPage()
    {
        InitializeComponent();
        _feishuEvents = new FeishuEventRuntime(_feishuCredentials);
        _runtime.StateChanged += OnRuntimeStateChanged;
        _feishuEvents.StateChanged += OnFeishuEventStateChanged;
        RenderRuntime(_runtime.Snapshot);
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        SetPromLightCommandsEnabled(deviceReady: false);
        RenderFeishuCredential(_feishuCredentials.Load());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RefreshEnvironmentAsync();
        await StartRuntimeAsync();
        await DetectPromLightAsync();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        await ShutdownAsync();
    }

    internal Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _loaded = false;
        _operationCancellation?.Cancel();
        _promLightCancellation?.Cancel();
        _feishuCancellation?.Cancel();
        await _feishuEvents.DisposeAsync();
        await _runtime.StopAsync();
        await _promLight.DisposeAsync();
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

    private async void OnFeishuVerifyClicked(object sender, RoutedEventArgs e)
    {
        _feishuCancellation?.Cancel();
        _feishuCancellation?.Dispose();
        _feishuCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        SetFeishuCommandsEnabled(enabled: false);
        FeishuCredentialStatus.Title = "正在验证";
        FeishuCredentialStatus.Message = "正在向飞书官方认证端点验证测试机器人凭证…";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;

        try
        {
            FeishuVerificationResult result = await _feishuCredentials.VerifyAndSaveAsync(
                FeishuAppIdTextBox.Text,
                FeishuAppSecretPasswordBox.Password,
                _feishuCancellation.Token);
            FeishuAppSecretPasswordBox.Password = string.Empty;
            FeishuCredentialStatus.Title = result.Success ? "测试机器人凭证有效" : "凭证验证未通过";
            FeishuCredentialStatus.Message = result.Success && result.ExpiresInSeconds is int expires
                ? $"{result.Message} 有效期约 {expires} 秒。"
                : result.Message;
            FeishuCredentialStatus.Severity = result.Success
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            FeishuClearButton.IsEnabled = result.Success || _feishuCredentials.Load().HasSecret;
            FeishuConnectButton.IsEnabled = result.Success;
            if (result.Success)
            {
                _feishuEvents.RefreshOwnerBindingState();
            }
        }
        catch (OperationCanceledException)
        {
            FeishuCredentialStatus.Title = "验证超时";
            FeishuCredentialStatus.Message = "没有在 15 秒内取得飞书认证响应；凭证未被更新。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            FeishuAppSecretPasswordBox.Password = string.Empty;
            SetFeishuCommandsEnabled(enabled: true);
        }
    }

    private void OnFeishuClearClicked(object sender, RoutedEventArgs e)
    {
        if (_feishuEvents.Snapshot.State is not FeishuEventRuntimeState.Stopped
            and not FeishuEventRuntimeState.Faulted)
        {
            FeishuCredentialStatus.Title = "请先停止消息长连接";
            FeishuCredentialStatus.Message = "停止长连接后才能清除它正在使用的本机凭证。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
            return;
        }

        _feishuCredentials.Clear();
        FeishuAppIdTextBox.Text = string.Empty;
        FeishuAppSecretPasswordBox.Password = string.Empty;
        FeishuCredentialStatus.Title = "Bridge 凭证已清除";
        FeishuCredentialStatus.Message =
            "Windows Credential Locker 中的 Bridge 测试凭证已删除；历史 lark-cli 的 deepori-windows-test profile 不参与当前 SDK 回复通路，也未被删除，需使用 CLI 单独显式清理。";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        FeishuClearButton.IsEnabled = false;
        _feishuEvents.RefreshOwnerBindingState();
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
    }

    private async void OnFeishuConnectClicked(object sender, RoutedEventArgs e)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _feishuEvents.StartAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            FeishuEventStatusText.Text = "△ 飞书消息长连接启动超时";
        }
        catch
        {
            RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        }
    }

    private async void OnFeishuDisconnectClicked(object sender, RoutedEventArgs e)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _feishuEvents.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            FeishuEventStatusText.Text = "△ 停止飞书消息长连接超时";
        }
    }

    private void OnFeishuReplyTestClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _feishuEvents.ArmApprovedTestReply();
        }
        catch (InvalidOperationException exception)
        {
            FeishuReplyStatusText.Text = $"△ {exception.Message}";
        }
    }

    private void OnFeishuOwnerBindClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _feishuEvents.ArmOwnerBinding();
        }
        catch (InvalidOperationException exception)
        {
            FeishuOwnerStatusText.Text = $"△ {exception.Message}";
        }
    }

    private void OnFeishuOwnerClearClicked(object sender, RoutedEventArgs e)
    {
        _feishuEvents.ClearOwnerBinding();
    }

    private void OnFeishuCodexRouteCaptureClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _feishuEvents.ArmCodexRouteCapture();
        }
        catch (InvalidOperationException exception)
        {
            FeishuCodexRouteStatusText.Text = $"△ {exception.Message}";
        }
    }

    private void OnFeishuCodexRouteClearClicked(object sender, RoutedEventArgs e)
    {
        _feishuEvents.ClearCodexRouteCapture();
    }

    private async void OnFeishuCodexRouteExecuteClicked(object sender, RoutedEventArgs e)
    {
        FeishuRouteAuthorization authorization;
        try
        {
            authorization = _feishuEvents.ClaimCodexRouteExecution();
        }
        catch (InvalidOperationException exception)
        {
            RenderFeishuEventRuntime(_feishuEvents.Snapshot);
            FeishuCodexRouteStatusText.Text = $"△ {exception.Message}";
            return;
        }

        _codexRouteExecuting = true;
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        FeishuCodexRouteStatusText.Text = "◌ 正在创建 read-only Codex 测试任务并等待 turn/completed…";

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            authorization.SessionCancellation);
        try
        {
            CodexControlledRouteResult result = await _runtime.RunControlledRouteTestAsync(linked.Token);
            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Codex turn 结束状态为 {result.Status}。");
            }

            if (!string.Equals(
                result.AgentMessageText.Trim(),
                BridgeRuntime.ControlledRouteExpectedResponse,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex 返回内容未通过固定文本校验，因此没有发送 Bot 成功回执。");
            }

            FeishuCodexRouteStatusText.Text = "◌ Codex 固定回复已验证，正在发送已授权的 Bot 回执…";
            await _feishuEvents.ReplyToCapturedCodexRouteAsync(authorization, linked.Token);
            FeishuCodexRouteStatusText.Text = "✓ Codex 路由测试完成；固定回复已验证，Bot 回执已发送。";
        }
        catch (OperationCanceledException)
        {
            FeishuCodexRouteStatusText.Text = "△ Codex 路由测试超时；未确认成功，未继续发送回执。";
        }
        catch (Exception exception)
        {
            FeishuCodexRouteStatusText.Text = $"✗ {exception.Message}";
        }
        finally
        {
            _feishuEvents.AbortRouteExecution(authorization);
            _codexRouteExecuting = false;
            FeishuCodexRouteExecuteButton.IsEnabled = _feishuEvents.Snapshot.CodexRouteCapturePending
                && _runtime.Snapshot.State == BridgeRuntimeState.Running;
            FeishuCodexRouteClearButton.IsEnabled = _feishuEvents.Snapshot.CodexRouteCaptureArmed
                || _feishuEvents.Snapshot.CodexRouteCapturePending;
        }
    }

    private void OnFeishuReadOnlyRouteCaptureClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _feishuEvents.ArmReadOnlyRouteCapture();
        }
        catch (InvalidOperationException exception)
        {
            FeishuReadOnlyRouteStatusText.Text = $"△ {exception.Message}";
        }
    }

    private void OnFeishuReadOnlyRouteClearClicked(object sender, RoutedEventArgs e)
    {
        _feishuEvents.ClearReadOnlyRouteCapture();
    }

    private async void OnFeishuReadOnlyRouteExecuteClicked(object sender, RoutedEventArgs e)
    {
        FeishuRouteAuthorization authorization;
        try
        {
            authorization = _feishuEvents.ClaimReadOnlyRouteExecution();
        }
        catch (InvalidOperationException exception)
        {
            RenderFeishuEventRuntime(_feishuEvents.Snapshot);
            FeishuReadOnlyRouteStatusText.Text = $"△ {exception.Message}";
            return;
        }

        string prompt = authorization.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            _feishuEvents.AbortRouteExecution(authorization);
            FeishuReadOnlyRouteStatusText.Text = "△ 只读授权票据没有有效提示词。";
            return;
        }

        _readOnlyRouteExecuting = true;
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        FeishuReadOnlyRouteStatusText.Text = "◌ 正在新建 read-only Codex Task 并等待 turn/completed…";

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            authorization.SessionCancellation);
        try
        {
            CodexControlledRouteResult result = await _runtime.RunReadOnlyPromptAsync(
                prompt,
                linked.Token);
            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Codex turn 结束状态为 {result.Status}。");
            }

            if (string.IsNullOrWhiteSpace(result.AgentMessageText))
            {
                throw new InvalidOperationException("Codex 没有返回可发送的文本；未发送飞书回复。");
            }

            FeishuReadOnlyRouteStatusText.Text = "◌ Codex Task 已完成，正在发送 owner-only 飞书回复…";
            await _feishuEvents.ReplyToCapturedReadOnlyRouteAsync(
                authorization,
                result.AgentMessageText,
                linked.Token);
            FeishuReadOnlyRouteStatusText.Text = "✓ 通用只读路由完成；Codex 回复已发送。";
        }
        catch (OperationCanceledException)
        {
            FeishuReadOnlyRouteStatusText.Text = "△ 通用只读路由超时；未确认成功，未继续发送回复。";
        }
        catch (Exception exception)
        {
            FeishuReadOnlyRouteStatusText.Text = $"✗ {exception.Message}";
        }
        finally
        {
            _feishuEvents.AbortRouteExecution(authorization);
            _readOnlyRouteExecuting = false;
            FeishuEventRuntimeSnapshot current = _feishuEvents.Snapshot;
            FeishuReadOnlyRouteExecuteButton.IsEnabled = current.ReadOnlyRouteCapturePending
                && _runtime.Snapshot.State == BridgeRuntimeState.Running;
            FeishuReadOnlyRouteClearButton.IsEnabled = current.ReadOnlyRouteCaptureArmed
                || current.ReadOnlyRouteCapturePending;
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
        CodexPathText.Text = snapshot.CodexExecutablePath;

        AppServerStatusText.Text = snapshot.AppServerInitialized
            ? $"✓ 初始化成功 · {snapshot.AppServerPlatform}"
            : "✗ 初始化未成功";
        ThreadListStatusText.Text = snapshot.ThreadListSucceeded
            ? $"✓ thread/list 只读请求成功（本页返回 {snapshot.ReturnedThreadCount} 条）"
            : "thread/list 尚未验证";

        StateDatabaseStatusText.Text = snapshot.StateDatabaseIsValid
            ? "✓ state_5.sqlite 是有效 SQLite 数据库"
            : "✗ state_5.sqlite 不存在或不可读";
        StateDatabasePathText.Text = snapshot.StateDatabasePath;
        CatalogDatabaseStatusText.Text = snapshot.CatalogDatabaseIsValid
            ? "✓ codex-dev.db 是有效 SQLite 数据库"
            : "✗ codex-dev.db 不存在或不可读";
        CatalogDatabasePathText.Text = snapshot.CatalogDatabasePath;

        PrivatePipeStatusText.Text = snapshot.PrivatePipeDetected
            ? "✓ 检测到 Windows 命名管道 \\.\\pipe\\codex-ipc"
            : "○ 当前未检测到 \\.\\pipe\\codex-ipc";
        LastCheckedText.Text = $"检查时间 {snapshot.CheckedAt:HH:mm:ss}";

        bool ready = snapshot.StateDatabaseIsValid
            && snapshot.CatalogDatabaseIsValid
            && snapshot.AppServerInitialized
            && snapshot.AppServerPlatform.Equals("windows/windows", StringComparison.OrdinalIgnoreCase)
            && snapshot.ThreadListSucceeded;

        if (ready)
        {
            OverallStatus.Title = "Windows Codex 核心通路已验证";
            OverallStatus.Message = "数据库路径有效，官方 app-server 已确认 Windows 平台，线程列表只读通路可响应。";
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
            BridgeRuntimeState.Starting => "◌ 正在启动官方 app-server 会话…",
            BridgeRuntimeState.Running => $"✓ 会话运行中 · {snapshot.Platform}",
            BridgeRuntimeState.Stopping => "◌ 正在停止会话…",
            BridgeRuntimeState.Faulted => "✗ 会话发生错误",
            _ => "○ 已停止",
        };

        RuntimeDetailText.Text = snapshot.State switch
        {
            BridgeRuntimeState.Running =>
                $"thread/list 最近返回 {snapshot.ThreadCount} 条；收到 {snapshot.NotificationCount} 个通知"
                + (string.IsNullOrWhiteSpace(snapshot.LastNotificationMethod)
                    ? "。"
                    : $"；最近方法 {snapshot.LastNotificationMethod}。"),
            BridgeRuntimeState.Faulted => snapshot.Error ?? "未知错误。",
            BridgeRuntimeState.Stopped => "官方 app-server stdio 子进程已关闭并清理。",
            _ => "生命周期操作进行中。",
        };

        bool transitioning = snapshot.State is BridgeRuntimeState.Starting or BridgeRuntimeState.Stopping;
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

    private void RenderFeishuCredential(FeishuStoredCredential credential)
    {
        FeishuAppIdTextBox.Text = credential.AppId;
        FeishuAppSecretPasswordBox.Password = string.Empty;

        if (!string.IsNullOrWhiteSpace(credential.Error))
        {
            FeishuCredentialStatus.Title = "无法读取安全存储";
            FeishuCredentialStatus.Message = credential.Error;
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        else if (credential.HasSecret)
        {
            FeishuCredentialStatus.Title = "已保存测试机器人凭证";
            FeishuCredentialStatus.Message = "App Secret 未显示；可留空并点击“保存并验证”重新验证。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        }
        else
        {
            FeishuCredentialStatus.Title = "等待凭证";
            FeishuCredentialStatus.Message = "请输入已发布测试机器人的 App ID 和 App Secret。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        }

        FeishuClearButton.IsEnabled = credential.HasSecret;
    }

    private void SetFeishuCommandsEnabled(bool enabled)
    {
        FeishuVerifyButton.IsEnabled = enabled;
        FeishuClearButton.IsEnabled = enabled && _feishuCredentials.Load().HasSecret;
        FeishuAppIdTextBox.IsEnabled = enabled;
        FeishuAppSecretPasswordBox.IsEnabled = enabled;
    }

    private void OnFeishuEventStateChanged(FeishuEventRuntimeSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => RenderFeishuEventRuntime(snapshot));
    }

    private void RenderFeishuEventRuntime(FeishuEventRuntimeSnapshot snapshot)
    {
        FeishuEventStatusText.Text = snapshot.State switch
        {
            FeishuEventRuntimeState.Configuring => "◌ 正在准备飞书 SDK 会话…",
            FeishuEventRuntimeState.Connecting => "◌ 正在启动官方 Go SDK WebSocket sidecar…",
            FeishuEventRuntimeState.Listening => "✓ 飞书消息长连接正在监听",
            FeishuEventRuntimeState.Stopping => "◌ 正在停止飞书消息长连接…",
            FeishuEventRuntimeState.Faulted => $"✗ {snapshot.Error ?? "飞书消息长连接失败。"}",
            _ => "○ 消息长连接未启动",
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

        FeishuReplyStatusText.Text = snapshot.ReplyError is not null
            ? $"✗ {snapshot.ReplyError}"
            : snapshot.ReplyArmed
                ? "已准备：只回复下一条规范化后等于“Windows Bridge 回执测试”的单聊消息。"
                : snapshot.ReplySentCount > 0
                    ? $"✓ Bot 回执已发送 {snapshot.ReplySentCount} 次"
                        + (snapshot.LastReplyAt is DateTimeOffset replyAt
                            ? $"；最近时间 {replyAt:HH:mm:ss}。"
                            : "。")
                    : "测试回执未准备。";

        FeishuOwnerStatusText.Text = snapshot.OwnerBindingError is not null
            ? $"✗ {snapshot.OwnerBindingError}"
            : snapshot.OwnerBindingArmed
                ? "已准备：只用下一条规范化后等于“Windows Bridge 绑定所有者”的单聊用户消息进行绑定。"
                : snapshot.OwnerBound
                    ? "✓ owner-only 白名单已绑定；sender ID 安全保存在 Windows Credential Locker。"
                    : "○ owner 尚未绑定；回执和 Codex 路由保持禁用。";

        FeishuCodexRouteStatusText.Text = snapshot.CodexRouteError is not null
            ? $"✗ {snapshot.CodexRouteError}"
            : snapshot.CodexRouteCapturePending
                ? "△ 已捕获路由测试消息，消息引用仅保留在内存；人工授权票据有效期 120 秒，授权前不会创建 Codex 任务或回复。"
                : snapshot.CodexRouteCaptureArmed
                    ? "已准备（等待期 120 秒）：优先识别“Windows Codex 路由测试”；下一条合规 owner 单聊即使正文格式不同，也只进入本机人工授权 Gate，不会自动执行。"
                    : snapshot.CodexRouteReplySentCount > 0
                        ? $"✓ Codex 路由与 Bot 回执已完成 {snapshot.CodexRouteReplySentCount} 次"
                            + (snapshot.LastCodexRouteReplyAt is DateTimeOffset routeReplyAt
                                ? $"；最近时间 {routeReplyAt:HH:mm:ss}。"
                                : "。")
                        : "路由测试未准备；thread/start 与 turn/start 不会被调用。";

        FeishuReadOnlyPromptPreview.Text = snapshot.ReadOnlyPromptPreview ?? string.Empty;
        FeishuReadOnlyRouteStatusText.Text = snapshot.ReadOnlyRouteError is not null
            ? $"✗ {snapshot.ReadOnlyRouteError}"
            : snapshot.ReadOnlyRouteCapturePending
                ? "△ 已捕获只读提示词并仅保留在内存；请核对预览，并在 120 秒内由本机人工点击执行。"
                : snapshot.ReadOnlyRouteCaptureArmed
                    ? "已准备（等待期 120 秒）：等待 owner 发送下一条以“Windows Codex：”开头的单聊文本。"
                    : snapshot.ReadOnlyRouteReplySentCount > 0
                        ? $"✓ 通用只读路由与飞书回复已完成 {snapshot.ReadOnlyRouteReplySentCount} 次"
                            + (snapshot.LastReadOnlyRouteReplyAt is DateTimeOffset readOnlyReplyAt
                                ? $"；最近时间 {readOnlyReplyAt:HH:mm:ss}。"
                                : "。")
                        : "只读路由未准备；不会自动创建 Codex Task 或发送飞书回复。";

        bool transitioning = snapshot.State is FeishuEventRuntimeState.Configuring
            or FeishuEventRuntimeState.Connecting
            or FeishuEventRuntimeState.Stopping;
        bool hasCredential = _feishuCredentials.Load().HasSecret;
        bool canStart = snapshot.State is FeishuEventRuntimeState.Stopped
            or FeishuEventRuntimeState.Faulted;
        FeishuConnectButton.IsEnabled = !transitioning && canStart && hasCredential;
        FeishuDisconnectButton.IsEnabled = !transitioning
            && snapshot.State == FeishuEventRuntimeState.Listening;
        FeishuReplyTestButton.IsEnabled = !transitioning
            && snapshot.State == FeishuEventRuntimeState.Listening
            && snapshot.OwnerBound
            && !snapshot.ReplyArmed;
        FeishuOwnerBindButton.IsEnabled = !transitioning
            && snapshot.State == FeishuEventRuntimeState.Listening
            && !snapshot.OwnerBound
            && !snapshot.OwnerBindingArmed;
        FeishuOwnerClearButton.IsEnabled = !transitioning
            && snapshot.OwnerBound;
        FeishuCodexRouteCaptureButton.IsEnabled = !transitioning
            && snapshot.State == FeishuEventRuntimeState.Listening
            && snapshot.OwnerBound
            && !snapshot.CodexRouteCaptureArmed
            && !snapshot.CodexRouteCapturePending;
        FeishuCodexRouteClearButton.IsEnabled = snapshot.CodexRouteCaptureArmed
            || snapshot.CodexRouteCapturePending;
        FeishuCodexRouteExecuteButton.IsEnabled = !_codexRouteExecuting
            && snapshot.CodexRouteCapturePending
            && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        FeishuReadOnlyRouteCaptureButton.IsEnabled = !transitioning
            && snapshot.State == FeishuEventRuntimeState.Listening
            && snapshot.OwnerBound
            && !snapshot.ReadOnlyRouteCaptureArmed
            && !snapshot.ReadOnlyRouteCapturePending;
        FeishuReadOnlyRouteClearButton.IsEnabled = snapshot.ReadOnlyRouteCaptureArmed
            || snapshot.ReadOnlyRouteCapturePending;
        FeishuReadOnlyRouteExecuteButton.IsEnabled = !_readOnlyRouteExecuting
            && snapshot.ReadOnlyRouteCapturePending
            && _runtime.Snapshot.State == BridgeRuntimeState.Running;
    }

    private static string PromLightStatusLabel(PromLightStatus status) => status switch
    {
        PromLightStatus.Idle => "空闲",
        PromLightStatus.Running => "运行中",
        PromLightStatus.HumanGate => "等待人工",
        PromLightStatus.Error => "错误",
        _ => status.ToString(),
    };
}
