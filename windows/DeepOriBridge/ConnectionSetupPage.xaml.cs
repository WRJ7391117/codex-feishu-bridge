using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeepOriBridge;

public sealed partial class ConnectionSetupPage : Page
{
    private readonly FeishuCredentialService _credentials;
    private readonly FeishuEventRuntime _events;
    private readonly CodexSetupSkillInstaller _skillInstaller;
    private CancellationTokenSource? _operationCancellation;
    private int _currentStep = 1;
    private bool _credentialVerified;
    private bool _botConfigurationVerified;
    private bool _projectAuthorized;
    private bool _codexProtocolAvailable;
    private bool _loaded;

    public ConnectionSetupPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _credentials = services.FeishuCredentials;
        _events = services.FeishuEvents;
        _skillInstaller = services.SetupSkillInstaller;
        CodexPromptTextBox.Text = WindowsSetupCopy.CodexPrompt
            + $"\n\n目标 Bridge instance session nonce：{services.ProductIdentity.SessionNonce}。"
            + " 每次 UI 操作前必须重新核对；任何不同或缺失 nonce 都要立即停止。";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _events.StateChanged += OnFeishuStateChanged;
        FeishuStoredCredential stored = _credentials.Load();
        SetupAppIdTextBox.Text = stored.AppId;
        ClearCredentialButton.IsEnabled = stored.HasSecret;
        _credentialVerified = stored.HasSecret
            && stored.HasVerifiedIdentity
            && string.IsNullOrWhiteSpace(stored.Error);
        if (_credentialVerified)
        {
            CredentialStatus.Title = "已恢复本机凭据状态";
            CredentialStatus.Message = $"App Secret 仍只保存在 Credential Locker 中且不会回填。已验证 App ID 指纹：{stored.AppFingerprint}";
            CredentialStatus.Severity = InfoBarSeverity.Informational;
        }
        else if (stored.HasSecret)
        {
            CredentialStatus.Title = "已有凭据，但 App 身份未验证";
            CredentialStatus.Message = "旧 Credential Locker 条目不会被静默信任。请重新输入 Secret，验证后才允许继续或启动长连接。";
            CredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        FeishuProjectAuthorizationState project = _credentials.LoadProjectAuthorization();
        _projectAuthorized = project.IsAuthorized;
        ProjectPathTextBox.Text = project.ProjectPath;
        RenderProjectAuthorization(project);
        RenderOwnerAndConnection(_events.Snapshot);
        _codexProtocolAvailable = await IsCodexProtocolAvailableAsync();
        CopyAndOpenCodexButton.IsEnabled = _codexProtocolAvailable;
        UpdateManualStep();
        BackButton.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _events.StateChanged -= OnFeishuStateChanged;
        _operationCancellation?.Cancel();
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

    private async void OnChooseCodexClicked(object sender, RoutedEventArgs e)
    {
        ShowSetupPath(CodexView);
        CodexSkillStatus.Title = "Codex 自动配置已安全阻断";
        CodexSkillStatus.Message = "跨应用身份契约尚未通过独立审计；不会查找、初始化或启动任何 DeepOri 应用。请使用手动向导。";
        CodexSkillStatus.Severity = InfoBarSeverity.Warning;
        CopyAndOpenCodexButton.IsEnabled = false;
        CopyCodexPromptButton.IsEnabled = false;
        await Task.CompletedTask;
    }

    private void OnChooseManualClicked(object sender, RoutedEventArgs e)
    {
        ShowSetupPath(ManualView);
        UpdateManualStep();
    }

    private void OnReturnToChoiceClicked(object sender, RoutedEventArgs e) =>
        ShowSetupPath(ChoiceView);

    private void ShowSetupPath(UIElement selected)
    {
        ChoiceView.Visibility = selected == ChoiceView ? Visibility.Visible : Visibility.Collapsed;
        CodexView.Visibility = selected == CodexView ? Visibility.Visible : Visibility.Collapsed;
        ManualView.Visibility = selected == ManualView ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Focus(FocusState.Programmatic);
    }

    private async void OnCopyAndOpenCodexClicked(object sender, RoutedEventArgs e)
    {
        if (!CopyPrompt())
        {
            return;
        }

        bool opened = _codexProtocolAvailable
            && await Launcher.LaunchUriAsync(new Uri("codex://"));
        CodexSkillStatus.Title = opened ? "指令已复制，Codex 已打开" : "指令已复制";
        CodexSkillStatus.Message = opened
            ? "请在任意 Codex Task 中粘贴并发送。"
            : "无法可靠激活 Codex Desktop；请手动打开后粘贴并发送。";
        CodexSkillStatus.Severity = opened
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    private void OnCopyCodexPromptClicked(object sender, RoutedEventArgs e)
    {
        if (CopyPrompt())
        {
            CodexSkillStatus.Title = "配置指令已复制";
            CodexSkillStatus.Message = "请在任意 Codex Task 中粘贴并发送。";
            CodexSkillStatus.Severity = InfoBarSeverity.Success;
        }
    }

    private bool CopyPrompt()
    {
        try
        {
            DataPackage package = new();
            package.SetText(WindowsSetupCopy.CodexPrompt);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception exception)
        {
            CodexSkillStatus.Title = "复制失败";
            CodexSkillStatus.Message = $"无法写入 Windows 剪贴板：{exception.Message}";
            CodexSkillStatus.Severity = InfoBarSeverity.Error;
            return false;
        }
    }

    private static async Task<bool> IsCodexProtocolAvailableAsync()
    {
        try
        {
            LaunchQuerySupportStatus status = await Launcher.QueryUriSupportAsync(
                new Uri("codex://"),
                LaunchQuerySupportType.Uri);
            return status == LaunchQuerySupportStatus.Available
                && !string.IsNullOrWhiteSpace(CodexExecutableLocator.Find());
        }
        catch
        {
            return false;
        }
    }

    private async void OnOpenDeveloperConsoleClicked(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://open.feishu.cn/app"));

    private async void OnShowChecklistClicked(object sender, RoutedEventArgs e)
    {
        StackPanel content = new() { Spacing = 9 };
        foreach (string item in WindowsSetupCopy.ConfigurationChecklist)
        {
            content.Children.Add(new TextBlock
            {
                Text = "• " + item,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "完整配置清单",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = content,
            },
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private void OnStepButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button
            && int.TryParse(button.Tag?.ToString(), out int step)
            && step <= _currentStep)
        {
            _currentStep = step;
            UpdateManualStep();
        }
    }

    private void OnManualBackClicked(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateManualStep();
        }
        else
        {
            ShowSetupPath(ChoiceView);
        }
    }

    private void OnManualNextClicked(object sender, RoutedEventArgs e)
    {
        if (!CanAdvance())
        {
            return;
        }

        if (_currentStep == 4)
        {
            NavigateBack();
            return;
        }

        _currentStep++;
        UpdateManualStep();
    }

    private void OnCompletionInputChanged(object sender, RoutedEventArgs e) => UpdateManualStep();

    private async void OnSetupAppIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        FeishuStoredCredential stored = _credentials.Load();
        if (SetupAppIdTextBox.Text.Trim().Equals(stored.AppId, StringComparison.Ordinal))
        {
            return;
        }

        _credentialVerified = false;
        _botConfigurationVerified = false;
        CredentialStatus.Title = "App ID 已改变，身份立即失效";
        CredentialStatus.Message = "旧身份、owner 与项目授权不会用于这个 App ID。请重新输入 Secret 并验证。";
        CredentialStatus.Severity = InfoBarSeverity.Warning;
        if (_events.Snapshot.State == FeishuEventRuntimeState.Listening)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _events.StopAsync(timeout.Token); } catch { }
        }
        UpdateManualStep();
    }

    private void OnSaveProjectAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        FeishuProjectAuthorizationState result = _credentials.SaveProjectAuthorization(
            ProjectPathTextBox.Text);
        _projectAuthorized = result.IsAuthorized;
        ProjectPathTextBox.Text = result.ProjectPath.Length > 0
            ? result.ProjectPath
            : ProjectPathTextBox.Text;
        RenderProjectAuthorization(result);
        UpdateManualStep();
    }

    private void RenderProjectAuthorization(FeishuProjectAuthorizationState state)
    {
        ProjectAuthorizationStatus.Title = state.IsAuthorized ? "明确项目授权已保存" : "项目授权尚未完成";
        ProjectAuthorizationStatus.Message = state.IsAuthorized
            ? $"只允许当前绑定用户访问：{state.ProjectPath}"
            : state.Error ?? "必须保存一个明确存在的目录；不会默认开放所有项目。";
        ProjectAuthorizationStatus.Severity = state.IsAuthorized
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    private async void OnVerifyCredentialClicked(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        VerifyCredentialButton.IsEnabled = false;
        CredentialStatus.Title = "正在验证";
        CredentialStatus.Message = "正在调用飞书官方认证端点；临时 token 不保存。";
        CredentialStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            FeishuVerificationResult result = await _credentials.VerifyAndSaveAsync(
                SetupAppIdTextBox.Text,
                SetupAppSecretPasswordBox.Password,
                _operationCancellation.Token);
            _credentialVerified = result.Success;
            SetupAppSecretPasswordBox.Password = string.Empty;
            CredentialStatus.Title = result.Success ? "连接检查通过" : "连接检查失败";
            CredentialStatus.Message = result.Message;
            CredentialStatus.Severity = result.Success
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            if (result.Success)
            {
                CredentialStatus.Message += $" 已验证 App ID 指纹：{_credentials.Load().AppFingerprint}";
            }
            ClearCredentialButton.IsEnabled = _credentials.Load().HasSecret;
            _events.RefreshOwnerBindingState();
        }
        catch (OperationCanceledException)
        {
            CredentialStatus.Title = "验证超时";
            CredentialStatus.Message = "没有在 15 秒内取得官方认证响应；凭据未更新。";
            CredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            SetupAppSecretPasswordBox.Password = string.Empty;
            VerifyCredentialButton.IsEnabled = true;
            UpdateManualStep();
        }
    }

    private async void OnClearCredentialClicked(object sender, RoutedEventArgs e)
    {
        if (_events.Snapshot.State is not FeishuEventRuntimeState.Stopped
            and not FeishuEventRuntimeState.Faulted)
        {
            CredentialStatus.Title = "请先停止飞书连接";
            CredentialStatus.Message = "运行中的 sidecar 不会被隐式切换凭据。";
            CredentialStatus.Severity = InfoBarSeverity.Warning;
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
        _credentialVerified = false;
        _botConfigurationVerified = false;
        SetupAppIdTextBox.Text = string.Empty;
        SetupAppSecretPasswordBox.Password = string.Empty;
        CredentialStatus.Title = "本机凭据已清除";
        CredentialStatus.Message = "Bridge v2 Credential Locker 命名空间中的凭据、App 指纹、授权用户和项目授权已删除。";
        CredentialStatus.Severity = InfoBarSeverity.Informational;
        ClearCredentialButton.IsEnabled = false;
        _events.RefreshOwnerBindingState();
        UpdateManualStep();
    }

    private async void OnCheckBotConfigurationClicked(object sender, RoutedEventArgs e)
    {
        FeishuStoredCredential stored = _credentials.Load();
        if (!stored.HasSecret || !stored.HasVerifiedIdentity)
        {
            BotConfigurationStatus.Title = "缺少已验证 App 身份";
            BotConfigurationStatus.Message = "请返回第 2 步验证 App ID 与 App Secret，并确认页面显示 App ID 指纹。";
            BotConfigurationStatus.Severity = InfoBarSeverity.Warning;
            return;
        }

        CheckBotConfigurationButton.IsEnabled = false;
        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            FeishuVerificationResult result = await _credentials.VerifyAndSaveAsync(
                stored.AppId,
                string.Empty,
                _operationCancellation.Token);
            _botConfigurationVerified = result.Success;
            BotConfigurationStatus.Title = result.Success ? "基础连接检查通过" : "基础连接检查失败";
            BotConfigurationStatus.Message = result.Success
                ? "Bot 身份和飞书网络可用。权限、事件与版本仍以开放平台显示为准。"
                : result.Message;
            BotConfigurationStatus.Severity = result.Success
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
        }
        catch (OperationCanceledException)
        {
            BotConfigurationStatus.Title = "检查超时";
            BotConfigurationStatus.Message = "官方认证端点没有在限定时间内响应。";
            BotConfigurationStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            CheckBotConfigurationButton.IsEnabled = true;
            UpdateManualStep();
        }
    }

    private async void OnStartSetupConnectionClicked(object sender, RoutedEventArgs e)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await _events.StartAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            OwnerBindingStatus.Text = "连接超时；未确认长连接已启动。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OwnerBindingStatus.Text = "连接失败：" + exception.Message;
        }

        RenderOwnerAndConnection(_events.Snapshot);
    }

    private void OnArmOwnerBindingClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _events.ArmOwnerBinding();
        }
        catch (InvalidOperationException exception)
        {
            OwnerBindingStatus.Text = exception.Message;
        }
    }

    private void OnClearOwnerBindingClicked(object sender, RoutedEventArgs e) =>
        _events.ClearOwnerBinding();

    private void OnFeishuStateChanged(FeishuEventRuntimeSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(() => RenderOwnerAndConnection(snapshot));

    private void RenderOwnerAndConnection(FeishuEventRuntimeSnapshot snapshot)
    {
        FeishuProjectAuthorizationState project = _credentials.LoadProjectAuthorization();
        _projectAuthorized = project.IsAuthorized;
        if (_currentStep == 4)
        {
            RenderProjectAuthorization(project);
        }
        OwnerBindingStatus.Text = snapshot.OwnerBindingArmed
            ? $"请在两分钟内用需要添加的飞书账号单聊机器人，只发送一次性验证码：{snapshot.OwnerBindingCode}"
            : snapshot.OwnerBound
                ? $"已授权 {_credentials.LoadOwnerBinding().UserCount} 位用户；当前用户指纹 {_credentials.LoadOwnerBinding().ActiveUserFingerprint}。sender ID 不显示。"
                : snapshot.State == FeishuEventRuntimeState.Listening
                    ? "飞书已连接；尚未绑定 owner。"
                    : "尚未绑定 owner；请先启动飞书连接。";
        StartSetupConnectionButton.IsEnabled = snapshot.State is FeishuEventRuntimeState.Stopped
            or FeishuEventRuntimeState.Faulted;
        ArmOwnerBindingButton.IsEnabled = snapshot.State == FeishuEventRuntimeState.Listening
            && !snapshot.OwnerBindingArmed;
        ClearOwnerBindingButton.IsEnabled = snapshot.OwnerBound;
        UpdateManualStep();
    }

    private bool CanAdvance() => _currentStep switch
    {
        1 => Step1Confirmation.IsChecked == true,
        2 => _credentialVerified,
        3 => _botConfigurationVerified,
        4 => _events.Snapshot.OwnerBound && _projectAuthorized,
        _ => false,
    };

    private void UpdateManualStep()
    {
        ManualStep1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        ManualStep2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        ManualStep3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        ManualStep4Panel.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
        ManualStep1Button.IsEnabled = true;
        ManualStep2Button.IsEnabled = _currentStep >= 2;
        ManualStep3Button.IsEnabled = _currentStep >= 3;
        ManualStep4Button.IsEnabled = _currentStep >= 4;
        ManualBackButton.Content = _currentStep == 1 ? "返回选择" : "返回";
        ManualNextButton.Content = _currentStep == 4 ? "完成设置" : "继续";
        ManualNextButton.IsEnabled = CanAdvance();
        ManualStepCounter.Text = $"第 {_currentStep} 步，共 4 步";
        (ManualStatusTitle.Text, ManualStatusDetail.Text) = _currentStep switch
        {
            1 => ("完成后继续", "创建应用并确认管理权限后进入凭据连接。"),
            2 => (_credentialVerified ? "连接检查通过" : "等待连接检查", "只有官方认证成功后才能继续。"),
            3 => (_botConfigurationVerified ? "基础检查通过" : "等待开放平台配置", "发布、权限与事件仍需在飞书开放平台人工核对。"),
            _ => (_events.Snapshot.OwnerBound ? "owner 已绑定" : "等待 owner 绑定", "完成显式项目授权后才可结束向导。"),
        };
    }
}
