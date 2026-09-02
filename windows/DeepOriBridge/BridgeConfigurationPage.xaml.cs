using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeepOriBridge;

public sealed partial class BridgeConfigurationPage : Page
{
    private readonly FeishuCredentialService _credentials;
    private readonly FeishuEventRuntime _events;
    private readonly BridgeRuntime _runtime;
    private CancellationTokenSource? _credentialCancellation;
    private bool _loaded;
    private bool _codexRouteExecuting;
    private bool _readOnlyRouteExecuting;

    public BridgeConfigurationPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _credentials = services.FeishuCredentials;
        _events = services.FeishuEvents;
        _runtime = services.Runtime;
        RenderCredential(_credentials.Load());
        RenderProjectAuthorization(_credentials.LoadProjectAuthorization());
        RenderRuntime(_events.Snapshot);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _events.StateChanged += OnStateChanged;
        RenderRuntime(_events.Snapshot);
        BackButton.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _events.StateChanged -= OnStateChanged;
        _credentialCancellation?.Cancel();
    }

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

    private void OnBackClicked(object sender, RoutedEventArgs e) => NavigateBack();
    private void OnOpenSetupClicked(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ConnectionSetupPage));

    private void NavigateBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(MainPage));
    }

    private async void OnFeishuVerifyClicked(object sender, RoutedEventArgs e)
    {
        _credentialCancellation?.Cancel();
        _credentialCancellation?.Dispose();
        _credentialCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SetCredentialCommands(false);
        FeishuCredentialStatus.Title = "正在验证";
        FeishuCredentialStatus.Message = "正在向飞书官方认证端点验证凭据…";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            FeishuVerificationResult result = await _credentials.VerifyAndSaveAsync(
                FeishuAppIdTextBox.Text,
                FeishuAppSecretPasswordBox.Password,
                _credentialCancellation.Token);
            FeishuCredentialStatus.Title = result.Success ? "凭据有效" : "验证未通过";
            FeishuCredentialStatus.Message = result.Success && result.ExpiresInSeconds is int expires
                ? $"{result.Message} 有效期约 {expires} 秒。" : result.Message;
            FeishuCredentialStatus.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            if (result.Success)
            {
                FeishuCredentialStatus.Message += $" 已验证 App ID 指纹：{_credentials.Load().AppFingerprint}";
                _events.RefreshOwnerBindingState();
            }
        }
        catch (OperationCanceledException)
        {
            FeishuCredentialStatus.Title = "验证超时";
            FeishuCredentialStatus.Message = "没有在 15 秒内取得响应；凭据未更新。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            FeishuAppSecretPasswordBox.Password = string.Empty;
            SetCredentialCommands(true);
            RenderRuntime(_events.Snapshot);
        }
    }

    private async void OnFeishuClearClicked(object sender, RoutedEventArgs e)
    {
        if (_events.Snapshot.State is not FeishuEventRuntimeState.Stopped and not FeishuEventRuntimeState.Faulted)
        {
            FeishuCredentialStatus.Title = "请先停止连接";
            FeishuCredentialStatus.Message = "停止长连接后才能清除它正在使用的凭据。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置本机飞书数据？",
            Content = "将删除当前 Windows 用户下 Bridge 专属的 Bot 凭据、已验证 App 指纹、授权用户与项目授权。不会修改飞书开放平台、Mac Bridge 或 DeepOri Agent Mesh。",
            PrimaryButtonText = "重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _credentials.Clear();
        FeishuAppIdTextBox.Text = string.Empty;
        FeishuAppSecretPasswordBox.Password = string.Empty;
        FeishuCredentialStatus.Title = "本机凭据已清除";
        FeishuCredentialStatus.Message = "Bridge v2 Credential Locker 命名空间中的凭据、App 指纹、授权用户和项目授权已删除。";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        _events.RefreshOwnerBindingState();
        RenderRuntime(_events.Snapshot);
    }

    private async void OnFeishuConnectClicked(object sender, RoutedEventArgs e)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await _events.StartAsync(timeout.Token); }
        catch (OperationCanceledException) { FeishuEventStatusText.Text = "△ 启动超时"; }
        catch { RenderRuntime(_events.Snapshot); }
    }

    private async void OnFeishuDisconnectClicked(object sender, RoutedEventArgs e)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _events.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { FeishuEventStatusText.Text = "△ 停止超时"; }
    }

    private void OnFeishuReplyTestClicked(object sender, RoutedEventArgs e)
    {
        try { _events.ArmApprovedTestReply(); }
        catch (InvalidOperationException exception) { FeishuReplyStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuOwnerBindClicked(object sender, RoutedEventArgs e)
    {
        try { _events.ArmOwnerBinding(); }
        catch (InvalidOperationException exception) { FeishuOwnerStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuOwnerClearClicked(object sender, RoutedEventArgs e) => _events.ClearOwnerBinding();

    private void OnSaveProjectAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        FeishuProjectAuthorizationState result = _credentials.SaveProjectAuthorization(ProjectPathTextBox.Text);
        RenderProjectAuthorization(result);
        RenderRuntime(_events.Snapshot);
    }

    private async void OnAppIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        FeishuStoredCredential stored = _credentials.Load();
        if (FeishuAppIdTextBox.Text.Trim().Equals(stored.AppId, StringComparison.Ordinal))
        {
            return;
        }

        FeishuConnectButton.IsEnabled = false;
        FeishuCredentialStatus.Title = "App ID 已改变，应用身份立即失效";
        FeishuCredentialStatus.Message = "旧身份、owner 与项目授权不会用于这个 App ID。重新验证前禁止启动 sidecar。";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        if (_events.Snapshot.State == FeishuEventRuntimeState.Listening)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _events.StopAsync(timeout.Token); } catch { }
        }
    }

    private void OnFeishuCodexRouteCaptureClicked(object sender, RoutedEventArgs e)
    {
        try { _events.ArmCodexRouteCapture(); }
        catch (InvalidOperationException exception) { FeishuCodexRouteStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuCodexRouteClearClicked(object sender, RoutedEventArgs e) => _events.ClearCodexRouteCapture();

    private async void OnFeishuCodexRouteExecuteClicked(object sender, RoutedEventArgs e)
    {
        FeishuRouteAuthorization authorization;
        try { authorization = _events.ClaimCodexRouteExecution(); }
        catch (InvalidOperationException exception)
        {
            RenderRuntime(_events.Snapshot);
            FeishuCodexRouteStatusText.Text = $"△ {exception.Message}";
            return;
        }

        _codexRouteExecuting = true;
        RenderRuntime(_events.Snapshot);
        FeishuCodexRouteStatusText.Text = "◌ 正在创建 read-only Codex 测试任务…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, authorization.SessionCancellation);
        try
        {
            CodexControlledRouteResult result = await _runtime.RunControlledRouteTestAsync(
                authorization.ProjectPath,
                linked.Token);
            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal)
                || !string.Equals(result.AgentMessageText.Trim(), BridgeRuntime.ControlledRouteExpectedResponse, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex 固定回复未通过校验，因此没有发送 Bot 回执。");
            }
            await _events.ReplyToCapturedCodexRouteAsync(authorization, linked.Token);
            FeishuCodexRouteStatusText.Text = "✓ 固定回复已验证，Bot 回执已发送。";
        }
        catch (OperationCanceledException) { FeishuCodexRouteStatusText.Text = "△ 测试超时；未继续发送回执。"; }
        catch (Exception exception) { FeishuCodexRouteStatusText.Text = $"✗ {exception.Message}"; }
        finally
        {
            _events.AbortRouteExecution(authorization);
            _codexRouteExecuting = false;
            RenderRuntime(_events.Snapshot);
        }
    }

    private void OnFeishuReadOnlyRouteCaptureClicked(object sender, RoutedEventArgs e)
    {
        try { _events.ArmReadOnlyRouteCapture(); }
        catch (InvalidOperationException exception) { FeishuReadOnlyRouteStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuReadOnlyRouteClearClicked(object sender, RoutedEventArgs e) => _events.ClearReadOnlyRouteCapture();

    private async void OnFeishuReadOnlyRouteExecuteClicked(object sender, RoutedEventArgs e)
    {
        FeishuRouteAuthorization authorization;
        try { authorization = _events.ClaimReadOnlyRouteExecution(); }
        catch (InvalidOperationException exception)
        {
            RenderRuntime(_events.Snapshot);
            FeishuReadOnlyRouteStatusText.Text = $"△ {exception.Message}";
            return;
        }

        string prompt = authorization.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            _events.AbortRouteExecution(authorization);
            FeishuReadOnlyRouteStatusText.Text = "△ 授权票据没有有效提示词。";
            return;
        }

        _readOnlyRouteExecuting = true;
        RenderRuntime(_events.Snapshot);
        FeishuReadOnlyRouteStatusText.Text = "◌ 正在新建 read-only Codex Task…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, authorization.SessionCancellation);
        try
        {
            CodexControlledRouteResult result = await _runtime.RunReadOnlyPromptAsync(
                prompt,
                authorization.ProjectPath,
                linked.Token);
            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(result.AgentMessageText))
            {
                throw new InvalidOperationException("Codex 没有返回可发送的完成文本。" );
            }
            await _events.ReplyToCapturedReadOnlyRouteAsync(authorization, result.AgentMessageText, linked.Token);
            FeishuReadOnlyRouteStatusText.Text = "✓ 只读路由完成，Codex 回复已发送。";
        }
        catch (OperationCanceledException) { FeishuReadOnlyRouteStatusText.Text = "△ 路由超时；未继续发送回复。"; }
        catch (Exception exception) { FeishuReadOnlyRouteStatusText.Text = $"✗ {exception.Message}"; }
        finally
        {
            _events.AbortRouteExecution(authorization);
            _readOnlyRouteExecuting = false;
            RenderRuntime(_events.Snapshot);
        }
    }

    private void OnStateChanged(FeishuEventRuntimeSnapshot snapshot) => DispatcherQueue.TryEnqueue(() => RenderRuntime(snapshot));

    private void RenderCredential(FeishuStoredCredential credential)
    {
        FeishuAppIdTextBox.Text = credential.AppId;
        FeishuAppSecretPasswordBox.Password = string.Empty;
        if (!string.IsNullOrWhiteSpace(credential.Error))
        {
            FeishuCredentialStatus.Title = "无法读取安全存储";
            FeishuCredentialStatus.Message = credential.Error;
            FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            FeishuCredentialStatus.Title = credential.HasSecret && credential.HasVerifiedIdentity
                ? "本机已保存并验证 App 身份"
                : credential.HasSecret ? "已有凭据，但身份未验证" : "等待凭据";
            FeishuCredentialStatus.Message = credential.HasSecret && credential.HasVerifiedIdentity
                ? $"App Secret 不会回填。已验证 App ID 指纹：{credential.AppFingerprint}"
                : credential.HasSecret
                    ? "应用身份不匹配或缺少指纹；长连接保持禁止，请重新输入 Secret 完成验证。"
                    : "请输入已发布机器人的 App ID 和 App Secret。";
            FeishuCredentialStatus.Severity = credential.HasSecret && !credential.HasVerifiedIdentity
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
        }
        FeishuClearButton.IsEnabled = credential.HasSecret;
    }

    private void SetCredentialCommands(bool enabled)
    {
        FeishuVerifyButton.IsEnabled = enabled;
        FeishuClearButton.IsEnabled = enabled && _credentials.Load().HasSecret;
        FeishuAppIdTextBox.IsEnabled = enabled;
        FeishuAppSecretPasswordBox.IsEnabled = enabled;
    }

    private void RenderProjectAuthorization(FeishuProjectAuthorizationState authorization)
    {
        ProjectPathTextBox.Text = authorization.ProjectPath.Length > 0
            ? authorization.ProjectPath
            : ProjectPathTextBox.Text;
        ProjectAuthorizationStatus.Title = authorization.IsAuthorized ? "明确项目授权已保存" : "项目授权尚未完成";
        ProjectAuthorizationStatus.Message = authorization.IsAuthorized
            ? $"只允许当前绑定用户访问：{authorization.ProjectPath}"
            : authorization.Error ?? "必须保存一个明确存在的目录；不允许通配符或所有项目授权。";
        ProjectAuthorizationStatus.Severity = authorization.IsAuthorized
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    private void RenderRuntime(FeishuEventRuntimeSnapshot snapshot)
    {
        FeishuEventStatusText.Text = snapshot.State switch
        {
            FeishuEventRuntimeState.Configuring => "准备中",
            FeishuEventRuntimeState.Connecting => "连接中",
            FeishuEventRuntimeState.Listening => "✓ 已连接",
            FeishuEventRuntimeState.Stopping => "停止中",
            FeishuEventRuntimeState.Faulted => $"✗ {snapshot.Error ?? "连接失败"}",
            _ => "○ 未连接",
        };
        FeishuEventCountText.Text = $"消费者回调：消息 {snapshot.ReceivedEventCount} · 菜单 {snapshot.MenuEventCount} · 卡片 {snapshot.CardActionEventCount}。不保存正文与用户标识。";
        FeishuOwnerStatusText.Text = snapshot.OwnerBindingError is not null ? $"✗ {snapshot.OwnerBindingError}"
            : snapshot.OwnerBindingArmed ? $"两分钟内只发送一次性验证码：{snapshot.OwnerBindingCode}"
            : snapshot.OwnerBound
                ? $"✓ 已授权 {_credentials.LoadOwnerBinding().UserCount} 位用户；当前用户指纹 {_credentials.LoadOwnerBinding().ActiveUserFingerprint}。"
                : "○ 尚无授权用户。";
        FeishuReplyStatusText.Text = snapshot.ReplyError is not null ? $"✗ {snapshot.ReplyError}"
            : snapshot.ReplyArmed ? "已准备，只匹配下一条“Windows Bridge 回执测试”。"
            : snapshot.ReplySentCount > 0 ? $"✓ 已发送 {snapshot.ReplySentCount} 次测试回执。" : "测试回执未准备。";
        FeishuCodexRouteStatusText.Text = snapshot.CodexRouteError is not null ? $"✗ {snapshot.CodexRouteError}"
            : snapshot.CodexRouteCapturePending ? "△ 已捕获，仅内存保留；等待本机人工确认。"
            : snapshot.CodexRouteCaptureArmed ? "等待 owner 单聊测试消息（120 秒）。" : "固定测试未准备。";
        FeishuReadOnlyPromptPreview.Text = snapshot.ReadOnlyPromptPreview ?? string.Empty;
        FeishuReadOnlyRouteStatusText.Text = snapshot.ReadOnlyRouteError is not null ? $"✗ {snapshot.ReadOnlyRouteError}"
            : snapshot.ReadOnlyRouteCapturePending ? "△ 已捕获并仅保留在内存；请核对后人工确认。"
            : snapshot.ReadOnlyRouteCaptureArmed ? "等待“Windows Codex：”owner 单聊（120 秒）。" : "只读路由未准备。";

        bool transitioning = snapshot.State is FeishuEventRuntimeState.Configuring or FeishuEventRuntimeState.Connecting or FeishuEventRuntimeState.Stopping;
        bool listening = snapshot.State == FeishuEventRuntimeState.Listening;
        bool projectAuthorized = _credentials.HasAnyProjectAuthorization();
        FeishuStoredCredential stored = _credentials.Load();
        FeishuConnectButton.IsEnabled = !transitioning
            && !listening
            && stored.HasSecret
            && stored.HasVerifiedIdentity;
        FeishuDisconnectButton.IsEnabled = !transitioning && listening;
        FeishuOwnerBindButton.IsEnabled = listening && !snapshot.OwnerBindingArmed;
        FeishuOwnerClearButton.IsEnabled = !transitioning && snapshot.OwnerBound;
        FeishuReplyTestButton.IsEnabled = listening && snapshot.OwnerBound && !snapshot.ReplyArmed;
        FeishuCodexRouteCaptureButton.IsEnabled = listening && snapshot.OwnerBound && projectAuthorized && !snapshot.CodexRouteCaptureArmed && !snapshot.CodexRouteCapturePending;
        FeishuCodexRouteExecuteButton.IsEnabled = !_codexRouteExecuting && snapshot.CodexRouteCapturePending && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        FeishuCodexRouteClearButton.IsEnabled = snapshot.CodexRouteCaptureArmed || snapshot.CodexRouteCapturePending;
        FeishuReadOnlyRouteCaptureButton.IsEnabled = listening && snapshot.OwnerBound && projectAuthorized && !snapshot.ReadOnlyRouteCaptureArmed && !snapshot.ReadOnlyRouteCapturePending;
        FeishuReadOnlyRouteExecuteButton.IsEnabled = !_readOnlyRouteExecuting && snapshot.ReadOnlyRouteCapturePending && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        FeishuReadOnlyRouteClearButton.IsEnabled = snapshot.ReadOnlyRouteCaptureArmed || snapshot.ReadOnlyRouteCapturePending;
    }
}
