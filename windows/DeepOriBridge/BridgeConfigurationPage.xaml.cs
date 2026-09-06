using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace DeepOriBridge;

public sealed partial class BridgeConfigurationPage : Page
{
    private readonly FeishuCredentialService _credentials;
    private readonly FeishuEventRuntime _events;
    private readonly BridgeRuntime _runtime;
    private readonly BridgeProductIdentity _productIdentity;
    private readonly PageIdentityCommandGate _pageIdentityGate = new();
    private CancellationTokenSource? _credentialCancellation;
    private readonly BotSetupTransaction _botSetupTransaction;
    private bool _loaded;
    private bool _codexRouteExecuting;
    private bool _focusBotCredentialOnLoad;

    public BridgeConfigurationPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _credentials = services.FeishuCredentials;
        _events = services.FeishuEvents;
        _runtime = services.Runtime;
        _productIdentity = services.ProductIdentity;
        _botSetupTransaction = new BotSetupTransaction(_credentials.Load());
        RenderCredential(_credentials.Load());
        RenderProjectAuthorization(_credentials.LoadProjectAuthorization());
        RenderRuntime(_events.Snapshot);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _focusBotCredentialOnLoad = string.Equals(
            e.Parameter as string,
            FirstConnectionNavigationGate.BotCredentialFocusParameter,
            StringComparison.Ordinal);
        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        int bindingGeneration = _pageIdentityGate.BeginBinding();
        DisableSensitiveConfigurationCommands();
        try
        {
            await Task.Run(() =>
                _productIdentity.BindPageContract("DeepOriBridge.Configuration.Page"));
        }
        catch (Exception exception)
        {
            _pageIdentityGate.TryMarkFaulted(bindingGeneration);
            DisableSensitiveConfigurationCommands();
            FeishuCredentialStatus.Title = "Bridge 页面身份核验失败";
            FeishuCredentialStatus.Message = $"无法从后台观察线程绑定当前配置页：{exception.Message}。所有敏感配置命令保持关闭。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Error;
            return;
        }
        if (!_loaded || !_pageIdentityGate.TryMarkBound(bindingGeneration))
        {
            return;
        }

        _events.StateChanged += OnStateChanged;
        SetCredentialCommands(true);
        ProjectPathTextBox.IsEnabled = _pageIdentityGate.MayEnable(true);
        SaveProjectAuthorizationButton.IsEnabled = _pageIdentityGate.MayEnable(true);
        RenderRuntime(_events.Snapshot);
        if (_focusBotCredentialOnLoad)
        {
            FeishuCredentialStatus.Title = "先验证 Bot 身份";
            FeishuCredentialStatus.Message = "只有当前 Bot 身份世代验证成功后，才能进入首次连接向导。App Secret 只在此安全输入框中输入。";
            FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
            FeishuAppIdTextBox.Focus(FocusState.Programmatic);
        }
        else
        {
            BackButton.Focus(FocusState.Programmatic);
        }
    }

    private void DisableSensitiveConfigurationCommands()
    {
        FeishuConnectButton.IsEnabled = false;
        FeishuDisconnectButton.IsEnabled = false;
        FeishuOwnerBindButton.IsEnabled = false;
        FeishuOwnerClearButton.IsEnabled = false;
        FeishuVerifyButton.IsEnabled = false;
        FeishuClearButton.IsEnabled = false;
        FeishuAppIdTextBox.IsEnabled = false;
        FeishuAppSecretPasswordBox.IsEnabled = false;
        ProjectPathTextBox.IsEnabled = false;
        SaveProjectAuthorizationButton.IsEnabled = false;
        FeishuCodexRouteCaptureButton.IsEnabled = false;
        FeishuCodexRouteExecuteButton.IsEnabled = false;
        FeishuCodexRouteClearButton.IsEnabled = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _pageIdentityGate.Invalidate();
        DisableSensitiveConfigurationCommands();
        _events.StateChanged -= OnStateChanged;
        _credentialCancellation?.Cancel();
        _ = _botSetupTransaction.CancelMayRestorePriorPresentation(_credentials.Load());
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
    private void OnOpenSetupClicked(object sender, RoutedEventArgs e)
    {
        if (FirstConnectionNavigationGate.Resolve(_credentials.Load())
            == FirstConnectionDestination.FirstConnectionAssistant)
        {
            Frame.Navigate(typeof(ConnectionSetupPage));
            return;
        }

        FeishuCredentialStatus.Title = "当前 Bot 身份尚未验证";
        FeishuCredentialStatus.Message = "请先在本页完成官方 bot-info 验证；首次连接向导保持关闭。";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
        FeishuAppIdTextBox.Focus(FocusState.Programmatic);
    }

    private void NavigateBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(MainPage));
    }

    private async void OnFeishuVerifyClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        _credentialCancellation?.Cancel();
        _credentialCancellation?.Dispose();
        _credentialCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SetCredentialCommands(false);
        FeishuConnectButton.IsEnabled = false;
        FeishuCredentialStatus.Title = "正在验证";
        FeishuCredentialStatus.Message = "正在向飞书官方认证端点验证凭据…";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            FeishuVerificationResult result = await _events.VerifyAndSaveIdentityAsync(
                FeishuAppIdTextBox.Text,
                FeishuAppSecretPasswordBox.Password,
                _credentialCancellation.Token);
            _botSetupTransaction.ObserveVerification(result, _credentials.Load());
            FeishuCredentialStatus.Title = result.Success ? "凭据有效" : "验证未通过";
            FeishuCredentialStatus.Message = result.Success && result.ExpiresInSeconds is int expires
                ? $"{result.Message} 有效期约 {expires} 秒。" : result.Message;
            FeishuCredentialStatus.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            if (result.Success)
            {
                FeishuStoredCredential verified = _credentials.Load();
                FeishuCredentialStatus.Message += $" 已验证 Bot：{verified.BotDisplayName}；身份指纹：{verified.AppFingerprint}";
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;

        await _events.ClearIdentityAsync();
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        FeishuStoredCredential stored = _credentials.Load();
        if (!FeishuIdentityGenerationPolicy.MayStart(
                FeishuAppIdTextBox.Text,
                stored))
        {
            FeishuEventStatusText.Text = "△ UI 预期身份与 vault 已验证身份不一致";
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await _events.StartAsync(FeishuAppIdTextBox.Text, timeout.Token); }
        catch (OperationCanceledException) { FeishuEventStatusText.Text = "△ 启动超时"; }
        catch { RenderRuntime(_events.Snapshot); }
    }

    private async void OnFeishuDisconnectClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _events.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { FeishuEventStatusText.Text = "△ 停止超时"; }
    }

    private void OnFeishuOwnerBindClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        try { _events.ArmOwnerBinding(); }
        catch (InvalidOperationException exception) { FeishuOwnerStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuOwnerClearClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        _events.ClearOwnerBinding();
    }

    private void OnSaveProjectAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        FeishuProjectAuthorizationState result = _credentials.SaveProjectAuthorization(ProjectPathTextBox.Text);
        RenderProjectAuthorization(result);
        RenderRuntime(_events.Snapshot);
    }

    private void OnAppIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || !_pageIdentityGate.AllowsSensitiveActions)
        {
            return;
        }

        FeishuStoredCredential stored = _credentials.Load();
        if (FeishuAppIdTextBox.Text.Trim().Equals(stored.AppId, StringComparison.Ordinal))
        {
            return;
        }

        FeishuConnectButton.IsEnabled = false;
        FeishuCredentialStatus.Title = "正在编辑未提交的 Bot 草稿";
        FeishuCredentialStatus.Message = "旧的已验证身份与关联授权保持有效；只有新凭据验证且写入 Credential Locker 成功后才切换身份世代。";
        FeishuCredentialStatus.Severity = InfoBarSeverity.Warning;
    }

    private void OnFeishuCodexRouteCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        try { _events.ArmCodexRouteCapture(); }
        catch (InvalidOperationException exception) { FeishuCodexRouteStatusText.Text = $"△ {exception.Message}"; }
    }

    private void OnFeishuCodexRouteClearClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        _events.ClearCodexRouteCapture();
    }

    private async void OnFeishuCodexRouteExecuteClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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
                ? "已恢复当前 Windows 用户的 App 身份"
                : credential.HasSecret ? "已有凭据，但身份未验证" : "等待凭据";
            FeishuCredentialStatus.Message = credential.HasSecret && credential.HasVerifiedIdentity
                ? $"凭据来自当前 Windows 用户 Credential Locker，并非 App 内置默认值；App Secret 不会回填。已验证 Bot：{credential.BotDisplayName}；bot/tenant 身份指纹：{credential.AppFingerprint}"
                : credential.HasSecret
                    ? "应用身份不匹配或缺少指纹；长连接保持禁止，请重新输入 Secret 完成验证。"
                    : "未找到当前 Windows 用户的 Bridge vault 条目；App ID 与 App Secret 保持空白。请输入已发布机器人的凭据。";
            FeishuCredentialStatus.Severity = credential.HasSecret && !credential.HasVerifiedIdentity
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
        }
        FeishuClearButton.IsEnabled = _pageIdentityGate.MayEnable(credential.HasSecret);
    }

    private void SetCredentialCommands(bool enabled)
    {
        bool identityAllowsEnable = _pageIdentityGate.MayEnable(enabled);
        FeishuVerifyButton.IsEnabled = identityAllowsEnable;
        FeishuClearButton.IsEnabled = identityAllowsEnable && _credentials.Load().HasSecret;
        FeishuAppIdTextBox.IsEnabled = identityAllowsEnable;
        FeishuAppSecretPasswordBox.IsEnabled = identityAllowsEnable;
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
        string menuStatus = snapshot.MenuBusinessReplyError is { Length: > 0 }
            ? $"最近菜单处理失败：{snapshot.MenuBusinessReplyError}"
            : snapshot.MenuEventCount > 0
                ? "最近菜单事件已进入 owner-only 卡片处理。"
                : "尚未收到菜单事件。";
        FeishuEventCountText.Text = $"已认证消费者：{snapshot.RegisteredConsumerCount}/{snapshot.ExpectedConsumerCount}；原始回调计数：消息 {snapshot.ReceivedEventCount} · 菜单 {snapshot.MenuEventCount} · 卡片 {snapshot.CardActionEventCount}。不保存正文与用户标识。{menuStatus}";
        FeishuOwnerStatusText.Text = snapshot.OwnerBindingError is not null ? $"✗ {snapshot.OwnerBindingError}"
            : snapshot.OwnerBindingArmed ? $"两分钟内只发送一次性验证码：{snapshot.OwnerBindingCode}"
            : snapshot.OwnerBound
                ? $"✓ 已授权 {_credentials.LoadOwnerBinding().UserCount} 位用户；当前用户指纹 {_credentials.LoadOwnerBinding().ActiveUserFingerprint}。"
                : "○ 尚无授权用户。";
        FeishuReplyStatusText.Text = snapshot.ReplyError is not null ? $"✗ {snapshot.ReplyError}"
            : snapshot.ReplySentCount > 0 ? $"✓ 已发送 {snapshot.ReplySentCount} 次自动回执。" : "等待已绑定使用者的消息；无需本机准备。";
        FeishuCodexRouteStatusText.Text = snapshot.CodexRouteError is not null ? $"✗ {snapshot.CodexRouteError}"
            : snapshot.CodexRouteCapturePending ? "△ 已捕获，仅内存保留；等待本机人工确认。"
            : snapshot.CodexRouteCaptureArmed ? "等待 owner 单聊测试消息（120 秒）。" : "固定测试未准备。";
        FeishuReadOnlyRouteStatusText.Text = snapshot.ReadOnlyRouteError is not null ? $"✗ {snapshot.ReadOnlyRouteError}"
            : snapshot.ReadOnlyRouteExecuting || snapshot.ReadOnlyRouteCapturePending ? "◌ 已捕获，正在自动运行只读 Codex Task。"
            : snapshot.ReadOnlyRouteReplySentCount > 0 ? $"✓ 已自动完成并回复 {snapshot.ReadOnlyRouteReplySentCount} 条只读请求。"
            : "等待“Windows Codex：”开头的 owner 单聊；无需本机逐条确认。";

        bool transitioning = snapshot.State is FeishuEventRuntimeState.Configuring or FeishuEventRuntimeState.Connecting or FeishuEventRuntimeState.Stopping;
        bool listening = snapshot.State == FeishuEventRuntimeState.Listening;
        bool projectAuthorized = _credentials.HasAnyProjectAuthorization();
        FeishuStoredCredential stored = _credentials.Load();
        bool identityAllowsActions = _pageIdentityGate.AllowsSensitiveActions;
        FeishuConnectButton.IsEnabled = identityAllowsActions
            && !transitioning
            && !listening
            && _events.CanStartSensitiveRuntime
            && stored.HasSecret
            && stored.HasVerifiedIdentity;
        FeishuDisconnectButton.IsEnabled = identityAllowsActions && !transitioning && listening;
        FeishuOwnerBindButton.IsEnabled = identityAllowsActions && listening && !snapshot.OwnerBindingArmed;
        FeishuOwnerClearButton.IsEnabled = identityAllowsActions && !transitioning && snapshot.OwnerBound;
        FeishuCodexRouteCaptureButton.IsEnabled = identityAllowsActions && listening && snapshot.OwnerBound && projectAuthorized && !snapshot.CodexRouteCaptureArmed && !snapshot.CodexRouteCapturePending;
        FeishuCodexRouteExecuteButton.IsEnabled = identityAllowsActions && !_codexRouteExecuting && snapshot.CodexRouteCapturePending && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        FeishuCodexRouteClearButton.IsEnabled = identityAllowsActions && (snapshot.CodexRouteCaptureArmed || snapshot.CodexRouteCapturePending);
    }
}
