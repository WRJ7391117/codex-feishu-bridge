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
    private readonly BridgeSetupHandshakeService _setupHandshake;
    private readonly BridgeProductIdentity _productIdentity;
    private readonly BotSetupTransaction _botSetupTransaction;
    private CancellationTokenSource? _operationCancellation;
    private int _currentStep = 1;
    private bool _credentialVerified;
    private bool _botConfigurationVerified;
    private bool _projectAuthorized;
    private bool _codexProtocolAvailable;
    private bool _productIdentityBound;
    private bool _credentialVerificationInProgress;
    private bool _currentlyVerified;
    private bool _codexAssistedMode;
    private bool _codexHandshakeAccepted;
    private bool _challengeIssued;
    private bool _autoMenuPublishAuthorized;
    private long _credentialInputVersion;
    private bool _loaded;

    public ConnectionSetupPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _credentials = services.FeishuCredentials;
        _events = services.FeishuEvents;
        _skillInstaller = services.SetupSkillInstaller;
        _setupHandshake = services.SetupHandshake;
        _productIdentity = services.ProductIdentity;
        _botSetupTransaction = new BotSetupTransaction(_credentials.Load());
        _setupHandshake.HandshakeAccepted += OnCodexHandshakeAccepted;
        _setupHandshake.HandshakeUnavailable += OnCodexHandshakeUnavailable;
        _setupHandshake.CommandHandler = HandleCodexAssistedCommandAsync;
        CodexPromptTextBox.Text = WindowsSetupCopy.CodexPrompt
            + $"\n\n尚未签发握手 nonce。只有每次点击复制时由 App 通过当前用户专用 named pipe 签发的 {BridgeSetupHandshakeService.ChallengeLifetimeLabel}单次 nonce 才有效。";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        FeishuStoredCredential gateCredential = _credentials.Load();
        if (FirstConnectionNavigationGate.Resolve(gateCredential)
            != FirstConnectionDestination.FirstConnectionAssistant)
        {
            _loaded = false;
            Frame.Navigate(
                typeof(BridgeConfigurationPage),
                FirstConnectionNavigationGate.BotCredentialFocusParameter);
            return;
        }
        try
        {
            // UI Automation must not inspect this WinUI tree from its owning UI thread.
            // Run the independent page-contract observation on a worker thread after Loaded.
            await Task.Run(() =>
                _productIdentity.BindPageContract("DeepOriBridge.ConnectionSetup.Page"));
            _productIdentityBound = true;
            LocalPreviewSecurityStatus.Title = "本机预览身份核验通过";
            LocalPreviewSecurityStatus.Message = "已核验精确 Bridge 进程、窗口和首次连接页面。App Secret 只在 Bridge 中输入。";
            LocalPreviewSecurityStatus.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception)
        {
            _productIdentityBound = false;
            LocalPreviewSecurityStatus.Title = "Codex 辅助配置已安全阻断";
            LocalPreviewSecurityStatus.Message = $"无法绑定当前 Bridge 窗口：{exception.Message}。仍可使用手动向导。";
            LocalPreviewSecurityStatus.Severity = InfoBarSeverity.Error;
        }
        _events.StateChanged += OnFeishuStateChanged;
        FeishuStoredCredential stored = _credentials.Load();
        _currentlyVerified = FirstConnectionNavigationGate.IsCurrentlyVerified(stored);
        SetupAppIdTextBox.Text = stored.AppId;
        ClearCredentialButton.IsEnabled = stored.HasSecret;
        _credentialVerified = stored.HasSecret
            && stored.HasVerifiedIdentity
            && string.IsNullOrWhiteSpace(stored.Error);
        if (_credentialVerified)
        {
            CredentialStatus.Title = "已恢复本机凭据状态";
            CredentialStatus.Message = $"已从当前 Windows 用户 Credential Locker 恢复；并非 App 内置默认值。App Secret 不会回填。已验证 Bot：{stored.BotDisplayName}；bot/tenant 身份指纹：{stored.AppFingerprint}";
            CredentialStatus.Severity = InfoBarSeverity.Informational;
        }
        else if (stored.HasSecret)
        {
            CredentialStatus.Title = "已有凭据，但 App 身份未验证";
            CredentialStatus.Message = "旧 Credential Locker 条目不会被静默信任。请重新输入 Secret，验证后才允许继续或启动长连接。";
            CredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            CredentialStatus.Title = "当前 Windows 用户没有已保存凭据";
            CredentialStatus.Message = "未找到当前 Windows 用户的 Bridge vault 条目；App ID 与 App Secret 保持空白。请只输入已发布机器人的凭据。";
            CredentialStatus.Severity = InfoBarSeverity.Informational;
        }
        FeishuProjectAuthorizationState project = _credentials.LoadProjectAuthorization();
        _projectAuthorized = project.IsAuthorized;
        ProjectPathTextBox.Text = project.ProjectPath;
        RenderProjectAuthorization(project);
        RenderOwnerAndConnection(_events.Snapshot);
        _codexProtocolAvailable = await IsCodexProtocolAvailableAsync();
        UpdateSetupPathAvailability();
        UpdateManualStep();
        BackButton.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _setupHandshake.RevokeActiveChallenge();
        _setupHandshake.HandshakeAccepted -= OnCodexHandshakeAccepted;
        _setupHandshake.HandshakeUnavailable -= OnCodexHandshakeUnavailable;
        _setupHandshake.CommandHandler = null;
        _events.StateChanged -= OnFeishuStateChanged;
        _operationCancellation?.Cancel();
        _ = _botSetupTransaction.CancelMayRestorePriorPresentation(_credentials.Load());
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            HandleBackNavigation();
            e.Handled = true;
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => HandleBackNavigation();

    private void HandleBackNavigation()
    {
        if (_codexAssistedMode)
        {
            ExitCodexAssistedMode();
            return;
        }

        NavigateBack();
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

    private void OnChooseCodexClicked(object sender, RoutedEventArgs e)
    {
        if (!_credentialVerified || !_productIdentityBound)
        {
            ShowCredentialGate("请先在上方完成 Bot 身份验证，再使用 Codex 自动配置。");
            return;
        }

        SetCodexAssistedMode(true);
        ShowSetupPath(CodexView);
        CodexSetupSkillInstallResult installed = _skillInstaller.Install();
        CodexSetupSkillInstallResult verified = installed.Success
            ? _skillInstaller.VerifyInstalled()
            : installed;
        CodexSkillStatus.Title = verified.Success
            ? "Codex 本机配置助手已就绪"
            : "Codex 配置助手未就绪";
        CodexSkillStatus.Message = verified.Message;
        CodexSkillStatus.Severity = verified.Success
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Error;
        CopyAndOpenCodexButton.IsEnabled = verified.Success
            && _codexProtocolAvailable
            && _credentialVerified;
        CopyCodexPromptButton.IsEnabled = verified.Success && _credentialVerified;
    }

    private void OnChooseManualClicked(object sender, RoutedEventArgs e)
    {
        if (!_credentialVerified)
        {
            ShowCredentialGate("请先在上方完成 Bot 身份验证，再进入手动配置。");
            return;
        }

        ShowSetupPath(ManualView);
        RenderOwnerAndConnection(_events.Snapshot);
        UpdateManualStep();
    }

    private void OnOpenApplicationCreationGuidanceClicked(object sender, RoutedEventArgs e)
    {
        _currentStep = 1;
        ShowSetupPath(ManualView);
        UpdateManualStep();
    }

    private void ShowCredentialGate(string message)
    {
        ShowSetupPath(ChoiceView);
        CredentialStatus.Title = "需要先验证 Bot 身份";
        CredentialStatus.Message = message;
        CredentialStatus.Severity = InfoBarSeverity.Warning;
        SetupAppIdTextBox.Focus(FocusState.Programmatic);
    }

    private void UpdateSetupPathAvailability()
    {
        FeishuStoredCredential stored = _credentials.Load();
        _currentlyVerified = FirstConnectionNavigationGate.IsCurrentlyVerified(stored);
        bool storedCredentialVerified = stored.HasSecret
            && stored.HasVerifiedIdentity
            && string.IsNullOrWhiteSpace(stored.Error);
        SetupPathAvailability availability = SetupPathAvailabilityPolicy.Evaluate(
            storedCredentialVerified,
            SetupAppIdTextBox.Text,
            stored.AppId,
            _credentialVerificationInProgress,
            _productIdentityBound);
        _credentialVerified = availability.ManualEnabled;
        OpenApplicationCreationGuidanceButton.IsEnabled =
            availability.ApplicationCreationGuidanceEnabled;
        ChooseManualButton.IsEnabled = availability.ManualEnabled;
        ChooseCodexButton.IsEnabled = availability.CodexEnabled;
        if (!_credentialVerified)
        {
            CopyAndOpenCodexButton.IsEnabled = false;
            CopyCodexPromptButton.IsEnabled = false;
        }
    }

    private void OnReturnToChoiceClicked(object sender, RoutedEventArgs e)
    {
        SetCodexAssistedMode(false);
        ShowSetupPath(ChoiceView);
    }

    private void OnExitCodexAssistedClicked(object sender, RoutedEventArgs e) =>
        ExitCodexAssistedMode();

    private void ExitCodexAssistedMode()
    {
        _setupHandshake.RevokeActiveChallenge();
        _codexHandshakeAccepted = false;
        _challengeIssued = false;
        _autoMenuPublishAuthorized = false;
        SetCodexAssistedMode(false);
        ShowSetupPath(ChoiceView);
        LocalPreviewSecurityStatus.Title = "已退出 Codex 自动配置";
        LocalPreviewSecurityStatus.Message = "刚才的单次 nonce 已废止。现在可以重新选择自动配置或手动向导。";
        LocalPreviewSecurityStatus.Severity = InfoBarSeverity.Informational;
    }

    private void SetCodexAssistedMode(bool active)
    {
        _codexAssistedMode = active;
        ChoiceView.IsHitTestVisible = !active;
        ManualView.IsHitTestVisible = !active;
        CodexAssistedProgressRing.IsActive = active;
        CodexAssistedModeStatus.Title = active
            ? "Codex 自动配置进行中"
            : "Codex 自动配置已退出";
        CodexAssistedModeStatus.Message = active
            ? "Bridge 已锁定其他配置入口。请只完成 Codex 明确指出的人工 Gate；不要同时返回手动向导或修改配置。"
            : "当前自动配置会话已结束。";
    }

    private void OnCodexHandshakeAccepted() => DispatcherQueue.TryEnqueue(() =>
    {
        _codexHandshakeAccepted = true;
        _challengeIssued = true;
        CopyAndOpenCodexButton.IsEnabled = false;
        CopyCodexPromptButton.IsEnabled = false;
        CodexSkillStatus.Title = "身份已验证，自动配置继续中";
        CodexSkillStatus.Message = "握手已消费。Bridge 正等待 Codex 的受限状态读取或下一个人工 Gate；不会重新生成挑战。";
        CodexSkillStatus.Severity = InfoBarSeverity.Success;
        RenderCodexAssistedGate();
    });

    private void OnCodexHandshakeUnavailable(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_codexAssistedMode)
        {
            return;
        }

        _challengeIssued = false;
        _codexHandshakeAccepted = false;
        _autoMenuPublishAuthorized = false;
        bool enabled = _credentialVerified && _codexProtocolAvailable;
        CopyAndOpenCodexButton.IsEnabled = enabled;
        CopyCodexPromptButton.IsEnabled = _credentialVerified;
        CodexSkillStatus.Title = "身份握手未完成";
        CodexSkillStatus.Message = message;
        CodexSkillStatus.Severity = InfoBarSeverity.Warning;
    });

    private async Task<BridgeSetupCommandResponse> HandleCodexAssistedCommandAsync(
        BridgeSetupCommandRequest command, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BridgeSetupCommandResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await ExecuteCodexAssistedCommandAsync(command, cancellationToken));
            }
            catch
            {
                completion.TrySetResult(new(false, command.RequestId, "failed", "Bridge 未能完成受限自动配置操作。"));
            }
        }))
        {
            return new(false, command.RequestId, "denied", "Bridge UI 不再可用。" );
        }
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task<BridgeSetupCommandResponse> ExecuteCodexAssistedCommandAsync(
        BridgeSetupCommandRequest command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_codexAssistedMode || !_codexHandshakeAccepted || !_credentialVerified)
            return new(false, command.RequestId, "denied", "自动配置会话或身份世代未就绪。" );
        if (command.Operation == "feishu_listen")
        {
            FeishuStoredCredential stored = _credentials.Load();
            if (!FeishuIdentityGenerationPolicy.MayStart(SetupAppIdTextBox.Text, stored))
                return new(false, command.RequestId, "denied", "当前身份世代未通过。" );
            await _events.StartAsync(SetupAppIdTextBox.Text, cancellationToken);
        }
        else if (command.Operation == "remote_menu_status")
        {
            await _events.RefreshMenuStatusReadOnlyAsync(cancellationToken);
        }
        else if (command.Operation == "menu_inspect_apply")
        {
            await _events.BeginMenuSetupAsync(
                explicitSetupOperation: true,
                cancellationToken: cancellationToken);
        }
        else if (command.Operation == "menu_publish")
        {
            if (!_autoMenuPublishAuthorized)
                return new(false, command.RequestId, "denied", "本次自动配置没有菜单发布授权。");
            await _events.ConfirmAndPublishMenuAsync(
                explicitHumanConfirmation: true,
                cancellationToken: cancellationToken);
        }
        else if (command.Operation == "owner_gate" && !_events.Snapshot.OwnerBound)
        {
            _events.ArmOwnerBinding();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_codexAssistedMode || !_codexHandshakeAccepted || !_credentialVerified)
            return new(false, command.RequestId, "denied", "自动配置授权已撤销；请检查远端操作结果。");
        RenderOwnerAndConnection(_events.Snapshot);
        RenderCodexAssistedGate();
        string state = command.Operation switch
        {
            "remote_menu_status" => _events.Snapshot.MenuConfigurationStage.ToString(),
            "menu_inspect_apply" => _events.Snapshot.MenuConfigurationStage switch
            {
                FeishuMenuConfigurationStage.SubmittedPendingReview => "menu_submitted_pending_review",
                FeishuMenuConfigurationStage.PublishedPendingClientSync => "menu_published_pending_visibility",
                FeishuMenuConfigurationStage.VisibleVerified => "menu_visible_verified",
                FeishuMenuConfigurationStage.WrittenPendingPublish => "menu_candidate_ready",
                _ => "menu_candidate_not_ready",
            },
            "menu_publish" => _events.Snapshot.MenuConfigurationStage switch
            {
                FeishuMenuConfigurationStage.SubmittedPendingReview => "menu_submitted_pending_review",
                FeishuMenuConfigurationStage.PublishedPendingClientSync => "menu_published_pending_visibility",
                FeishuMenuConfigurationStage.VisibleVerified => "menu_visible_verified",
                _ => "menu_publish_not_complete",
            },
            "feishu_listen" => _events.Snapshot.State == FeishuEventRuntimeState.Listening ? "listening" : "not_listening",
            "owner_gate" => "owner_gate_armed",
            "project_gate" => "project_gate_required",
            "roundtrip_status" => _events.Snapshot.ReceivedEventCount > 0 && _events.Snapshot.ReplySentCount > 0
                ? "roundtrip_complete" : "roundtrip_required",
            _ => "setup_status",
        };
        string message = command.Operation is "menu_inspect_apply" or "menu_publish" or "remote_menu_status"
            ? _events.Snapshot.MenuConfigurationDetail
                ?? "菜单配置状态已更新；敏感标识不会通过管道返回。"
            : "受限自动配置状态已更新；敏感标识不会通过管道返回。";
        return new(true, command.RequestId, state, message);
    }

    private void RenderCodexAssistedGate()
    {
        if (!_codexAssistedMode || !_codexHandshakeAccepted) return;
        FeishuEventRuntimeSnapshot snapshot = _events.Snapshot;
        bool permissionGate = snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.Failed
            && snapshot.MenuConfigurationDetail?.Contains(
                "飞书菜单接口缺少 tenant scope",
                StringComparison.Ordinal) == true;
        OpenPermissionConsoleButton.Visibility = permissionGate
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.Failed)
        {
            CodexSkillStatus.Title = permissionGate
                ? "需要开通飞书菜单权限"
                : "自动菜单配置失败";
            CodexSkillStatus.Message = snapshot.MenuConfigurationDetail;
            CodexSkillStatus.Severity = permissionGate
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Error;
        }
        CodexOwnerGateButton.Visibility = !snapshot.OwnerBound && snapshot.State == FeishuEventRuntimeState.Listening
            ? Visibility.Visible : Visibility.Collapsed;
        CodexProjectGatePanel.Visibility = snapshot.OwnerBound && !_projectAuthorized
            ? Visibility.Visible : Visibility.Collapsed;
        CodexRoundTripStatus.Text = snapshot.ReceivedEventCount > 0 && snapshot.ReplySentCount > 0
            ? "真实文字往返已完成。"
            : "完成 owner 与项目授权后，请按 Codex 指示发送一次测试文字。";
        bool localGatesComplete = SetupCompletionGatePolicy.CanComplete(
                _currentlyVerified && _credentialVerified, snapshot.OwnerBound, _projectAuthorized,
                snapshot.RegisteredConsumerCount, snapshot.ExpectedConsumerCount,
                snapshot.ReceivedEventCount, snapshot.ReplySentCount);
        if (SetupCompletionGatePolicy.CanCompleteAutomatic(localGatesComplete, snapshot.MenuConfigurationStage))
        {
            SetCodexAssistedMode(false);
            CodexSkillStatus.Title = "自动配置已完成";
            CodexSkillStatus.Message = "身份、使用者、项目授权、真实文字往返与已发布菜单回调均已验证。PromLight 灯效需单独真机验证。";
            CodexSkillStatus.Severity = InfoBarSeverity.Success;
        }
    }

    private void OnCodexOwnerGateClicked(object sender, RoutedEventArgs e) => _events.ArmOwnerBinding();

    private void OnSaveCodexProjectAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        FeishuProjectAuthorizationState result = _credentials.SaveProjectAuthorization(CodexProjectPathTextBox.Text);
        _projectAuthorized = result.IsAuthorized;
        RenderCodexAssistedGate();
    }

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
            ? "请发送一次指令；Codex 将核验唯一 Bot，并自动推进权限、菜单和版本提交。"
            : "无法可靠激活 Codex Desktop；请手动打开后发送这一次指令，后续预期步骤会自动推进。";
        CodexSkillStatus.Severity = opened
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    private void OnCopyCodexPromptClicked(object sender, RoutedEventArgs e)
    {
        if (CopyPrompt())
        {
            CodexSkillStatus.Title = "配置指令已复制";
            CodexSkillStatus.Message = "请发送一次指令；它已绑定唯一 Bot，并授权自动完成预期权限、菜单和版本提交。";
            CodexSkillStatus.Severity = InfoBarSeverity.Success;
        }
    }

    private bool CopyPrompt()
    {
        if (_challengeIssued || _codexHandshakeAccepted)
        {
            CodexSkillStatus.Title = "现有自动配置会话仍有效";
            CodexSkillStatus.Message = "不会生成或复制第二个挑战；请继续当前会话，或明确退出后重启。";
            CodexSkillStatus.Severity = InfoBarSeverity.Warning;
            return false;
        }
        try
        {
            BridgeSetupChallenge challenge = _setupHandshake.IssueChallenge();
            FeishuStoredCredential target = _credentials.Load();
            string prompt = WindowsSetupCopy.BuildCodexPrompt(target)
                + BridgeSetupHandshakeService.BuildPromptSuffix(challenge);
            DataPackage package = new();
            package.SetText(prompt);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CodexPromptTextBox.Text = prompt;
            _challengeIssued = true;
            _autoMenuPublishAuthorized = true;
            SetCodexAssistedMode(true);
            CopyAndOpenCodexButton.IsEnabled = false;
            CopyCodexPromptButton.IsEnabled = false;
            CodexSkillStatus.Message = "已绑定唯一 Bot 并签发单次握手；发送指令后将自动完成预期权限、菜单和版本提交。";
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

        if (_currentStep == 1 && !_credentialVerified)
        {
            ShowSetupPath(ChoiceView);
            CredentialStatus.Title = "应用创建指导已完成";
            CredentialStatus.Message = "请在 Bridge 中输入刚创建应用的 App ID 与 App Secret，并完成官方 Bot 身份验证。";
            CredentialStatus.Severity = InfoBarSeverity.Informational;
            SetupAppIdTextBox.Focus(FocusState.Programmatic);
            return;
        }

        _currentStep++;
        UpdateManualStep();
    }

    private void OnCompletionInputChanged(object sender, RoutedEventArgs e) => UpdateManualStep();

    private void OnSetupAppIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        Interlocked.Increment(ref _credentialInputVersion);
        _operationCancellation?.Cancel();
        FeishuStoredCredential stored = _credentials.Load();
        if (SetupAppIdTextBox.Text.Trim().Equals(stored.AppId, StringComparison.Ordinal))
        {
            _credentialVerified = FirstConnectionNavigationGate.IsCurrentlyVerified(stored);
            _currentlyVerified = _credentialVerified;
            UpdateSetupPathAvailability();
            return;
        }

        _credentialVerified = false;
        _botConfigurationVerified = false;
        CredentialStatus.Title = "正在编辑未提交的 Bot 草稿";
        CredentialStatus.Message = "旧的已验证身份、owner、项目授权和运行时保持有效；只有新凭据验证且写入 Credential Locker 成功后才切换世代。";
        CredentialStatus.Severity = InfoBarSeverity.Warning;
        UpdateSetupPathAvailability();
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
        string intendedAppId = SetupAppIdTextBox.Text.Trim();
        long inputVersion = Volatile.Read(ref _credentialInputVersion);
        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _credentialVerificationInProgress = true;
        _credentialVerified = false;
        _botConfigurationVerified = false;
        UpdateSetupPathAvailability();
        VerifyCredentialButton.IsEnabled = false;
        StartSetupConnectionButton.IsEnabled = false;
        CredentialStatus.Title = "正在验证";
        CredentialStatus.Message = "正在调用飞书官方认证端点；临时 token 不保存。";
        CredentialStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            FeishuVerificationResult result = await _events.VerifyAndSaveIdentityAsync(
                intendedAppId,
                SetupAppSecretPasswordBox.Password,
                _operationCancellation.Token);
            FeishuStoredCredential storedAfterAttempt = _credentials.Load();
            _botSetupTransaction.ObserveVerification(result, storedAfterAttempt);
            _currentlyVerified = _botSetupTransaction.CurrentlyVerified;
            _credentialVerificationInProgress = false;
            UpdateSetupPathAvailability();
            bool exactInputStillVerified = result.Success
                && result.CredentialWriteSucceeded
                && result.CurrentlyVerified
                && inputVersion == Volatile.Read(ref _credentialInputVersion)
                && SetupAppIdTextBox.Text.Trim().Equals(
                    intendedAppId,
                    StringComparison.Ordinal);
            SetupAppSecretPasswordBox.Password = string.Empty;
            CredentialStatus.Title = exactInputStillVerified
                ? "连接检查通过"
                : result.Success
                    ? "输入已变化，需要重新验证"
                    : "连接检查失败";
            CredentialStatus.Message = result.Success && !exactInputStillVerified
                ? "验证返回时 App ID 已变化；本次结果不会开放任何配置入口。请使用当前 App ID 重新输入 Secret 并验证。"
                : result.Message;
            CredentialStatus.Severity = exactInputStillVerified
                ? InfoBarSeverity.Success
                : result.Success
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
            if (exactInputStillVerified)
            {
                _credentialVerified = true;
                FeishuStoredCredential verified = _credentials.Load();
                CredentialStatus.Message += $" 已验证 Bot：{verified.BotDisplayName}；身份指纹：{verified.AppFingerprint}";
            }
            else if (!result.CredentialWriteSucceeded)
            {
                _credentialVerified = _currentlyVerified
                    && SetupAppIdTextBox.Text.Trim().Equals(
                        storedAfterAttempt.AppId,
                        StringComparison.Ordinal);
            }
            ClearCredentialButton.IsEnabled = _credentials.Load().HasSecret;
            _events.RefreshOwnerBindingState();
        }
        catch (OperationCanceledException)
        {
            bool inputChanged = inputVersion != Volatile.Read(ref _credentialInputVersion);
            CredentialStatus.Title = inputChanged ? "输入已变化" : "验证超时";
            CredentialStatus.Message = inputChanged
                ? "App ID 已变化，本次验证已取消；请使用当前 App ID 重新输入 Secret 并验证。"
                : "没有在 15 秒内取得官方认证响应；凭据未更新。";
            CredentialStatus.Severity = InfoBarSeverity.Warning;
        }
        finally
        {
            _credentialVerificationInProgress = false;
            SetupAppSecretPasswordBox.Password = string.Empty;
            VerifyCredentialButton.IsEnabled = true;
            UpdateSetupPathAvailability();
            RenderOwnerAndConnection(_events.Snapshot);
            UpdateManualStep();
        }
    }

    private async void OnClearCredentialClicked(object sender, RoutedEventArgs e)
    {
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

        await _events.ClearIdentityAsync();
        _credentialVerified = false;
        _botConfigurationVerified = false;
        UpdateSetupPathAvailability();
        SetupAppIdTextBox.Text = string.Empty;
        SetupAppSecretPasswordBox.Password = string.Empty;
        CredentialStatus.Title = "本机凭据已清除";
        CredentialStatus.Message = "Bridge v2 Credential Locker 命名空间中的凭据、App 指纹、授权用户和项目授权已删除。";
        CredentialStatus.Severity = InfoBarSeverity.Informational;
        ClearCredentialButton.IsEnabled = false;
        _events.RefreshOwnerBindingState();
        UpdateManualStep();
    }

    private async void OnConfirmMenuPublishClicked(object sender, RoutedEventArgs e)
    {
        if (_events.Snapshot.MenuConfigurationStage
            is not (FeishuMenuConfigurationStage.WrittenPendingPublish
                or FeishuMenuConfigurationStage.PublishRejected))
        {
            RenderOwnerAndConnection(_events.Snapshot);
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "提交受管 Bot 菜单版本？",
            Content = "将重试提交固定的四组十二项悬浮菜单：Task 管理、桌面 Task、模型设置、提示灯。未知自定义菜单不会被覆盖。",
            PrimaryButtonText = "确认提交",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            await _events.ConfirmAndPublishMenuAsync(
                explicitHumanConfirmation: true,
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            BotConfigurationStatus.Title = "提交状态未确认";
            BotConfigurationStatus.Message = "请求超时；Bridge 不会把未知结果标记为已提交，请稍后重新读取远端状态。";
            BotConfigurationStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private async void OnStartSetupConnectionClicked(object sender, RoutedEventArgs e)
    {
        FeishuStoredCredential stored = _credentials.Load();
        if (!_credentialVerified
            || !FeishuIdentityGenerationPolicy.MayStart(
                SetupAppIdTextBox.Text,
                stored))
        {
            BotConfigurationStatus.Title = "身份世代未通过";
            BotConfigurationStatus.Message = "当前 App ID 与已验证 Bot 身份不一致；禁止启动 sidecar 或写入菜单。";
            BotConfigurationStatus.Severity = InfoBarSeverity.Error;
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        try
        {
            await _events.StartAsync(SetupAppIdTextBox.Text, timeout.Token);
            await _events.BeginMenuSetupAsync(
                explicitSetupOperation: true,
                cancellationToken: timeout.Token);
            if (_events.Snapshot.MenuConfigurationStage
                == FeishuMenuConfigurationStage.WrittenPendingPublish)
            {
                await _events.ConfirmAndPublishMenuAsync(
                    explicitHumanConfirmation: true,
                    cancellationToken: timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            BotConfigurationStatus.Title = "连接超时";
            BotConfigurationStatus.Message = "未确认长连接或菜单读取已启动。";
            BotConfigurationStatus.Severity = InfoBarSeverity.Warning;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BotConfigurationStatus.Title = "连接失败";
            BotConfigurationStatus.Message = exception.Message;
            BotConfigurationStatus.Severity = InfoBarSeverity.Error;
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
        _botConfigurationVerified = snapshot.MenuConfigurationStage
            is FeishuMenuConfigurationStage.PublishedPendingClientSync
                or FeishuMenuConfigurationStage.VisibleVerified;
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
        StartSetupConnectionButton.IsEnabled = _credentialVerified
            && _events.CanStartSensitiveRuntime
            && !snapshot.MenuConfigurationBusy
            && snapshot.State is (FeishuEventRuntimeState.Stopped
                or FeishuEventRuntimeState.Faulted
                or FeishuEventRuntimeState.Listening);
        ConfirmMenuPublishButton.IsEnabled = snapshot.MenuPublishConfirmationRequired
            && !snapshot.MenuConfigurationBusy
            && snapshot.State == FeishuEventRuntimeState.Listening;
        ConfirmMenuPublishButton.Visibility = snapshot.MenuConfigurationStage
                == FeishuMenuConfigurationStage.PublishRejected
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArmOwnerBindingButton.IsEnabled = snapshot.State == FeishuEventRuntimeState.Listening
            && !snapshot.OwnerBindingArmed;
        ClearOwnerBindingButton.IsEnabled = snapshot.OwnerBound;
        UpdateManualStep();
        RenderMenuConfiguration(snapshot);
        RenderCodexAssistedGate();
    }

    private void RenderMenuConfiguration(FeishuEventRuntimeSnapshot snapshot)
    {
        (BotConfigurationStatus.Title, BotConfigurationStatus.Severity) =
            snapshot.MenuConfigurationStage switch
            {
                FeishuMenuConfigurationStage.Inspecting => ("正在读取远端菜单", InfoBarSeverity.Informational),
                FeishuMenuConfigurationStage.Writing => ("正在写入受管菜单", InfoBarSeverity.Informational),
                FeishuMenuConfigurationStage.WrittenPendingPublish => ("菜单候选已写入，等待确认提交", InfoBarSeverity.Warning),
                FeishuMenuConfigurationStage.Publishing => ("正在提交应用版本", InfoBarSeverity.Informational),
                FeishuMenuConfigurationStage.SubmittedPendingReview => ("版本已提交，等待审核", InfoBarSeverity.Warning),
                FeishuMenuConfigurationStage.PublishRejected => ("版本审核被拒绝", InfoBarSeverity.Error),
                FeishuMenuConfigurationStage.PublishedPendingClientSync => ("版本已发布，等待客户端验收", InfoBarSeverity.Success),
                FeishuMenuConfigurationStage.VisibleVerified => ("菜单已在客户端完成真实验收", InfoBarSeverity.Success),
                FeishuMenuConfigurationStage.CustomConflict => ("发现自定义菜单，已安全停止", InfoBarSeverity.Error),
                FeishuMenuConfigurationStage.CapabilityGate => ("Mac 菜单语义尚未对齐，发布已阻止", InfoBarSeverity.Warning),
                FeishuMenuConfigurationStage.Failed => ("自动菜单配置失败", InfoBarSeverity.Error),
                _ => ("尚未读取远端菜单", InfoBarSeverity.Informational),
            };
        BotConfigurationStatus.Message = snapshot.MenuConfigurationDetail
            ?? "普通启动只读检查远端菜单；点击自动配置后，Bridge 才会写入并提交与 Mac 一致的四组十二项悬浮菜单。";
        StartSetupConnectionButton.Content = snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.Failed
            && snapshot.MenuConfigurationDetail?.Contains("飞书菜单接口缺少 tenant scope", StringComparison.Ordinal) == true
                ? "重试权限检查并配置菜单"
                : "启动连接并自动配置菜单";

        bool unlocked = !snapshot.MenuConfigurationBusy;
        BackButton.IsEnabled = unlocked;
        SetupContent.IsHitTestVisible = unlocked;
        SetupContent.Opacity = unlocked ? 1 : 0.72;
        ManualNextButton.IsEnabled = unlocked && CanAdvance();
    }

    private bool CanAdvance() => _currentStep switch
    {
        1 => Step1Confirmation.IsChecked == true,
        2 => _currentlyVerified && _credentialVerified,
        3 => _events.Snapshot.State == FeishuEventRuntimeState.Listening
            && _events.Snapshot.RegisteredConsumerCount
                == FeishuSidecarProtocol.ExpectedConsumers.Length,
        4 => _events.Snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.VisibleVerified
            && SetupCompletionGatePolicy.CanComplete(
            _currentlyVerified && _credentialVerified,
            _events.Snapshot.OwnerBound,
            _projectAuthorized,
            _events.Snapshot.RegisteredConsumerCount,
            _events.Snapshot.ExpectedConsumerCount,
            _events.Snapshot.ReceivedEventCount,
            _events.Snapshot.ReplySentCount),
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
            3 => (_events.Snapshot.RegisteredConsumerCount == FeishuSidecarProtocol.ExpectedConsumers.Length
                ? "三个消费者已独立就绪"
                : "等待三个消费者就绪",
                "菜单配置/发布是独立阶段，不会代替长连接消费者验收。"),
            _ => (_events.Snapshot.OwnerBound ? "owner 已绑定" : "等待 owner 绑定",
                "当前身份、owner、明确项目、3/3 消费者、真实文字往返以及已发布菜单点击与业务回复全部通过后才能完成。"),
        };
    }
}
