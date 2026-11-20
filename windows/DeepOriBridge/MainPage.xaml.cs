using DeepOriBridge.Models;
using DeepOriBridge.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeepOriBridge;

public sealed partial class MainPage : Page
{
    private readonly CodexEnvironmentProbe _probe;
    private readonly BridgeRuntime _runtime;
    private readonly PromLightFleetService _promLight;
    private readonly FeishuEventRuntime _feishuEvents;
    private readonly FeishuCredentialService _feishuCredentials;
    private readonly BridgeProductIdentity _productIdentity;
    private readonly PageIdentityCommandGate _pageIdentityGate = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _promLightCancellation;
    private CancellationTokenSource? _usageRefreshCancellation;
    private bool _loaded;
    private bool _gettingStartedRendered;
    private bool _promLightReady;
    private bool _renderingLamps;

    public MainPage()
    {
        InitializeComponent();
        BridgeAppServices services = ((App)Application.Current).Services;
        _probe = services.EnvironmentProbe;
        _runtime = services.Runtime;
        _promLight = services.PromLight;
        _feishuEvents = services.FeishuEvents;
        _feishuCredentials = services.FeishuCredentials;
        _productIdentity = services.ProductIdentity;
        RenderRuntime(_runtime.Snapshot);
        RenderCodexUsage(_runtime.UsageSnapshot);
        RenderFeishuEventRuntime(_feishuEvents.Snapshot);
        RenderGettingStarted();
        SetPromLightCommandsEnabled(deviceReady: false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        int bindingGeneration = _pageIdentityGate.BeginBinding();
        SetCommandsEnabled(false);
        SetPromLightCommandsEnabled(deviceReady: false);
        try
        {
            await Task.Run(() =>
                _productIdentity.BindPageContract("DeepOriBridge.Home.Page"));
        }
        catch (Exception exception)
        {
            _pageIdentityGate.TryMarkFaulted(bindingGeneration);
            SetCommandsEnabled(false);
            SetPromLightCommandsEnabled(deviceReady: false);
            OverallStatus.Title = "Bridge 页面身份核验失败";
            OverallStatus.Message = $"无法从后台观察线程绑定当前首页：{exception.Message}。运行时保持关闭。";
            OverallStatus.Severity = InfoBarSeverity.Error;
            return;
        }
        if (!_loaded || !_pageIdentityGate.TryMarkBound(bindingGeneration))
        {
            return;
        }

        _runtime.StateChanged += OnRuntimeStateChanged;
        _runtime.UsageChanged += OnUsageChanged;
        _feishuEvents.StateChanged += OnFeishuEventStateChanged;
        await RefreshEnvironmentAsync();
        await StartRuntimeAsync();
        await StartFeishuRuntimeIfConfiguredAsync();
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
        _pageIdentityGate.Invalidate();
        SetCommandsEnabled(false);
        SetPromLightCommandsEnabled(deviceReady: false);
        _runtime.StateChanged -= OnRuntimeStateChanged;
        _runtime.UsageChanged -= OnUsageChanged;
        _feishuEvents.StateChanged -= OnFeishuEventStateChanged;
        _operationCancellation?.Cancel();
        _promLightCancellation?.Cancel();
        _usageRefreshCancellation?.Cancel();
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await StartRuntimeAsync();
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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

    private async void OnCodexUsageRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions || !_loaded
            || _runtime.Snapshot.State != BridgeRuntimeState.Running
            || _usageRefreshCancellation is not null
            || _runtime.UsageSnapshot.Availability == CodexUsageAvailability.Loading) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _usageRefreshCancellation = timeout;
        UpdateUsageRefreshButton();
        try
        {
            // Explicit refresh bypasses the normal 60-second cache. No Task is started.
            await _runtime.RefreshUsageAsync(force: true, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (_loaded)
            {
                CodexUsageStatusText.Text = "刷新额度超时；未重置额度，可稍后重试。";
                AutomationProperties.SetName(CodexUsageStatusText, CodexUsageStatusText.Text);
            }
        }
        finally
        {
            _usageRefreshCancellation = null;
            UpdateUsageRefreshButton();
        }
    }

    private void UpdateUsageRefreshButton()
    {
        bool refreshing = _usageRefreshCancellation is not null
            || _runtime.UsageSnapshot.Availability == CodexUsageAvailability.Loading;
        CodexUsageRefreshLabel.Text = refreshing ? "刷新中…" : "刷新额度";
        CodexUsageRefreshButton.IsEnabled = _loaded && _pageIdentityGate.AllowsSensitiveActions
            && _runtime.Snapshot.State == BridgeRuntimeState.Running && !refreshing;
    }

    private async void OnPromLightDetectClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await DetectPromLightAsync();
    }

    private async Task RenderLampSelectorsAsync(CancellationToken token)
    {
        PromLightDiscoveredDevice[] devices = await _promLight.DiscoverDevicesAsync(token);
        _renderingLamps = true;
        try
        {
            PromLightOwnerSelector.Items.Clear();
            foreach (string owner in _feishuCredentials.BoundOwnerIds())
            {
                string label = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(owner)))[..8];
                var item = new ComboBoxItem { Content = "授权使用者 · " + label, Tag = owner };
                PromLightOwnerSelector.Items.Add(item);
                if (owner == _promLight.ActiveOwner) PromLightOwnerSelector.SelectedItem = item;
            }
            PromLightLampSelector.Items.Clear();
            PromLightLamp[] lamps = string.IsNullOrEmpty(_promLight.ActiveOwner) ? [] : _promLight.Owned(_promLight.ActiveOwner);
            string? selected = _promLight.Selected?.Id;
            foreach (var lamp in lamps)
            {
                var item = new ComboBoxItem { Content = lamp.Name + (lamp.PendingUnbind ? " · 等待清灯确认" : lamp.IsDefault ? " · 默认" : ""), Tag = lamp.Id, IsEnabled = !lamp.PendingUnbind };
                PromLightLampSelector.Items.Add(item);
                if (lamp.Id == selected) PromLightLampSelector.SelectedItem = item;
            }
            PromLightDeviceSelector.Items.Clear();
            foreach (var device in devices) PromLightDeviceSelector.Items.Add(new ComboBoxItem { Content = device.Label, Tag = device.Id });
            PromLightTasksText.Text = selected is null ? "尚未绑定灯。旧版单灯配置仍保留，不会自动分配给其他使用者。"
                : $"当前灯关联 {_promLight.Binding.TaskIds.Count} 个 Task。可在飞书“关联 Task”中逐个管理；默认灯只影响选择，不会广播或接管离线灯。";
        }
        finally { _renderingLamps = false; }
    }

    private async void OnPromLightOwnerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingLamps || !_pageIdentityGate.AllowsSensitiveActions || PromLightOwnerSelector.SelectedItem is not ComboBoxItem { Tag: string owner }) return;
        try { _promLight.SelectOwner(owner); await DetectPromLightAsync(); }
        catch (Exception exception) { PromLightWriteText.Text = exception.Message; }
    }
    private async void OnPromLightLampChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingLamps || !_pageIdentityGate.AllowsSensitiveActions || PromLightLampSelector.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        try { _promLight.Select(id); PromLightNameInput.Text = _promLight.Binding.Name; await DetectPromLightAsync(); }
        catch (Exception exception) { PromLightWriteText.Text = exception.Message; }
    }
    private async void OnPromLightBindClicked(object sender, RoutedEventArgs e) => await ChangeLampAsync(async token =>
    {
        if (PromLightDeviceSelector.SelectedItem is not ComboBoxItem { Tag: string id }) throw new InvalidOperationException("请明确选择物理设备。");
        await _promLight.BindAsync(id, PromLightNameInput.Text, token);
    });
    private async void OnPromLightRenameClicked(object sender, RoutedEventArgs e) => await ChangeLampAsync(token =>
    {
        var lamp = _promLight.Selected ?? throw new InvalidOperationException("请选择提示灯。");
        _promLight.Rename(lamp.Owner, lamp.Id, lamp.Revision, PromLightNameInput.Text); return Task.CompletedTask;
    });
    private async void OnPromLightImportClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "导入旧单灯配置？",
            Content = "仅导入原来明确绑定的设备，归属到当前所选使用者。每个旧 Task 都将重新检查项目权限，原配置不会删除。",
            PrimaryButtonText = "确认导入", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ChangeLampAsync(_promLight.ImportLegacyAsync);
    }
    private async void OnPromLightDefaultClicked(object sender, RoutedEventArgs e) => await ChangeLampAsync(token =>
    {
        var lamp = _promLight.Selected ?? throw new InvalidOperationException("请选择提示灯。");
        _promLight.MakeDefault(lamp.Owner, lamp.Id, lamp.Revision); return Task.CompletedTask;
    });
    private async void OnPromLightUnbindClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions || _promLight.Selected is not { } lamp) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "解绑“" + lamp.Name + "”？",
            Content = "将清除这盏灯的 Task 关联，并等待清灯写入确认后释放归属。不会删除 Windows 蓝牙配对。",
            PrimaryButtonText = "解绑", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ChangeLampAsync(token => _promLight.UnbindAsync(lamp.Owner, lamp.Id, lamp.Revision, token));
    }
    private async Task ChangeLampAsync(Func<CancellationToken, Task> action)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        try
        {
            SetPromLightCommandsEnabled(_promLightReady, busy: true);
            await action(ReplacePromLightCancellation(TimeSpan.FromSeconds(15)));
            await DetectPromLightAsync();
        }
        catch (Exception exception) { PromLightWriteText.Text = "操作未完成：" + exception.Message; }
        finally { SetPromLightCommandsEnabled(_promLightReady); }
    }

    private async void OnPromLightAssociateCurrentClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        CancellationToken token = ReplacePromLightCancellation(TimeSpan.FromSeconds(12));
        SetPromLightCommandsEnabled(_promLightReady, busy: true);
        try
        {
            CodexThreadSummary? current = await _runtime.GetCurrentThreadAsync(token);
            if (current is null)
            {
                PromLightWriteText.Text = "当前没有可关联的 Codex Task。";
                return;
            }
            _promLight.SetTaskAssociation(current.ThreadId, enabled: true);
            await _promLight.RefreshAsync(token);
            PromLightActivitySnapshot activity = _promLight.Activity;
            PromLightWriteText.Text =
                $"✓ 已关联当前 Task：{current.Title}。自动状态：{PromLightStatusLabel(activity.Status)}"
                + (activity.SubmittedAt is DateTimeOffset submittedAt
                    ? $"；HID 已提交 {activity.BytesWritten} 字节（{submittedAt:HH:mm:ss}）"
                    : string.Empty)
                + "。后续无需手动切换灯光。";
        }
        catch (OperationCanceledException)
        {
            PromLightWriteText.Text = "△ 关联当前 Task 超时；没有确认自动灯态。";
        }
        catch (Exception exception)
        {
            PromLightWriteText.Text = $"✗ 无法关联当前 Task：{exception.Message}";
        }
        finally
        {
            SetPromLightCommandsEnabled(_promLightReady);
        }
    }

    private async void OnPromLightIdleClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await SendPromLightAsync(PromLightStatus.Idle);
    }

    private async void OnPromLightRunningClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await SendPromLightAsync(PromLightStatus.Running);
    }

    private async void OnPromLightHumanGateClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await SendPromLightAsync(PromLightStatus.HumanGate);
    }

    private async void OnPromLightErrorClicked(object sender, RoutedEventArgs e)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        await SendPromLightAsync(PromLightStatus.Error);
    }

    private void OnOpenSetupClicked(object sender, RoutedEventArgs e)
    {
        if (FirstConnectionNavigationGate.Resolve(_feishuCredentials.Load())
            == FirstConnectionDestination.FirstConnectionAssistant)
        {
            NavigateTo(typeof(ConnectionSetupPage), "首次连接向导");
            return;
        }

        NavigateTo(
            typeof(BridgeConfigurationPage),
            "Bot 凭据验证",
            FirstConnectionNavigationGate.BotCredentialFocusParameter);
    }

    private async void OnOpenFeishuPlatformClicked(object sender, RoutedEventArgs e)
    {
        bool opened = await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://open.feishu.cn/app"));
        if (!opened)
        {
            OverallStatus.Title = "无法打开飞书开放平台";
            OverallStatus.Message = "请在浏览器中打开 open.feishu.cn/app，创建企业自建应用并启用机器人能力。";
            OverallStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private void OnOpenConfigurationClicked(object sender, RoutedEventArgs e) =>
        NavigateTo(typeof(BridgeConfigurationPage), "桥接配置");

    private void OnOpenDiagnosticsClicked(object sender, RoutedEventArgs e) =>
        NavigateTo(typeof(DiagnosticsPage), "运行诊断");

    private void NavigateTo(Type pageType, string pageName, object? parameter = null)
    {
        try
        {
            if (!Frame.Navigate(pageType, parameter))
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
        CancellationToken token = ReplacePromLightCancellation(TimeSpan.FromSeconds(10));
        _promLightReady = false;
        SetPromLightCommandsEnabled(deviceReady: false, busy: true);
        PromLightStatusText.Text = "正在通过 Windows HID 枚举 PromLight…";
        PromLightWriteText.Text = "检测过程只打开设备，不发送控制帧。";

        try
        {
            await RenderLampSelectorsAsync(token);
            PromLightDeviceSnapshot snapshot = await _promLight.DiscoverAsync(token);
            bool supported = snapshot.Opened
                && snapshot.ReleaseNumber == PromLightProtocol.SupportedReleaseNumber;
            _promLightReady = supported;
            SetPromLightCommandsEnabled(supported);

            if (supported)
            {
                PromLightStatusText.Text =
                    $"✓ 已打开 {snapshot.Product} {snapshot.DeviceVersion}（发现 {snapshot.DeviceCount} 台）";
                PromLightHardwareText.Text =
                    $"VID 07D7 · PID 6B01 · REV {snapshot.ReleaseNumber:X4} · Usage FF00:0001 · Report ID 2";
                int associatedCount = _promLight.Binding.TaskIds.Count;
                PromLightWriteText.Text = associatedCount > 0
                    ? $"自动提示灯已启用，正在跟随 {associatedCount} 个 Task。"
                    : "设备已就绪。关联当前 Task，或在飞书“我的提示灯”中选择要跟随的 Task。";
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
        catch (Exception exception)
        {
            PromLightStatusText.Text = "提示灯暂不可用";
            PromLightWriteText.Text = exception is InvalidOperationException ? exception.Message
                : "提示灯配置或授权无法读取；原数据已保留，设备控制已停止。";
            SetPromLightCommandsEnabled(deviceReady: false);
        }
    }

    private async Task SendPromLightAsync(PromLightStatus status)
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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

    private async Task StartFeishuRuntimeIfConfiguredAsync()
    {
        if (!_pageIdentityGate.AllowsSensitiveActions
            || _feishuEvents.Snapshot.State is FeishuEventRuntimeState.Configuring
                or FeishuEventRuntimeState.Connecting
                or FeishuEventRuntimeState.Listening)
        {
            return;
        }

        FeishuStoredCredential stored = ((App)Application.Current)
            .Services
            .FeishuCredentials
            .Load();
        if (!stored.HasSecret || !stored.HasVerifiedIdentity)
        {
            return;
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                await _feishuEvents.StartAsync(stored.AppId, timeout.Token);
                return;
            }
            catch (OperationCanceledException) when (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (
                attempt == 1
                && _feishuEvents.Snapshot.State == FeishuEventRuntimeState.Faulted)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // The runtime snapshot already records the safe, user-visible stop reason.
            }
            catch (Exception) when (_feishuEvents.Snapshot.State == FeishuEventRuntimeState.Faulted)
            {
                // Identity/runtime failures are projected by FeishuEventRuntime without secrets.
            }
        }
    }

    private async Task RefreshEnvironmentAsync()
    {
        if (!_pageIdentityGate.AllowsSensitiveActions) return;
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
            OverallStatus.Message = "已连接这台电脑上的 Codex Desktop；在飞书选择 Task 后可直接继续对话。";
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

    private void OnUsageChanged(CodexUsageSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(() => RenderCodexUsage(snapshot));

    private void RenderCodexUsage(CodexUsageSnapshot snapshot)
    {
        UpdateUsageRefreshButton();
        CodexUsageItems.Children.Clear();
        CodexUsageStatusText.Text = snapshot.Availability switch
        {
            CodexUsageAvailability.Loading => "正在从已验证的 Codex app-server 读取用量…",
            CodexUsageAvailability.Fresh when snapshot.UpdatedAt is DateTimeOffset updated =>
                $"已更新 {updated:HH:mm:ss}；仅显示服务端返回的额度窗口。",
            CodexUsageAvailability.Stale when snapshot.UpdatedAt is DateTimeOffset updated =>
                $"暂时无法刷新；保留 {updated:HH:mm:ss} 的最近成功数据。",
            CodexUsageAvailability.Stale => "暂时无法刷新；保留最近成功数据。",
            _ => "暂时无法刷新；没有可显示的额度数据。",
        };
        AutomationProperties.SetName(CodexUsageStatusText, CodexUsageStatusText.Text);
        if (snapshot.Buckets.Count == 0)
        {
            return;
        }

        foreach (CodexUsageBucket bucket in snapshot.Buckets)
        {
            var bucketPanel = new StackPanel { Spacing = 5 };
            string bucketName = $"{bucket.LimitName} · {bucket.PlanType}";
            var title = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = bucketName };
            AutomationProperties.SetName(title, $"额度组：{bucketName}");
            bucketPanel.Children.Add(title);
            foreach (CodexUsageWindow window in bucket.Windows)
            {
                string reset = window.ResetsAt is DateTimeOffset resetAt
                    ? $"本地重置 {resetAt:yyyy-MM-dd HH:mm}"
                    : "未提供重置时间";
                string severity = window.RemainingPercent <= 10 ? "剩余很低"
                    : window.RemainingPercent <= 30 ? "剩余偏低" : "剩余正常";
                string label = $"{window.Label}：剩余 {window.RemainingPercent:0}%；{reset}；{severity}";
                var row = new StackPanel { Spacing = 2 };
                var value = new TextBlock
                {
                    Text = label,
                    Foreground = UsageForeground(window.RemainingPercent),
                    TextWrapping = TextWrapping.Wrap,
                };
                AutomationProperties.SetName(value, label);
                var progress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = window.RemainingPercent,
                    Foreground = UsageForeground(window.RemainingPercent),
                };
                AutomationProperties.SetName(progress, label);
                row.Children.Add(value);
                row.Children.Add(progress);
                bucketPanel.Children.Add(row);
            }
            CodexUsageItems.Children.Add(bucketPanel);
        }
    }

    private static SolidColorBrush UsageForeground(double remaining) => new(
        remaining <= 10 ? Colors.Red : remaining <= 30 ? Colors.DarkOrange : Colors.ForestGreen);

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
        StartButton.IsEnabled = _pageIdentityGate.MayEnable(
            !transitioning && snapshot.State != BridgeRuntimeState.Running);
        StopButton.IsEnabled = _pageIdentityGate.MayEnable(
            !transitioning && snapshot.State == BridgeRuntimeState.Running);
        RefreshButton.IsEnabled = _pageIdentityGate.MayEnable(!transitioning);
        UpdateUsageRefreshButton();
    }

    private void SetCommandsEnabled(bool enabled)
    {
        bool identityAllowsEnable = _pageIdentityGate.MayEnable(enabled);
        StartButton.IsEnabled = identityAllowsEnable;
        StopButton.IsEnabled = identityAllowsEnable
            && _runtime.Snapshot.State == BridgeRuntimeState.Running;
        RefreshButton.IsEnabled = identityAllowsEnable;
        CodexUsageRefreshButton.IsEnabled = identityAllowsEnable && _loaded
            && _runtime.Snapshot.State == BridgeRuntimeState.Running
            && _usageRefreshCancellation is null
            && _runtime.UsageSnapshot.Availability != CodexUsageAvailability.Loading;
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
        bool identityAllowsEnable = _pageIdentityGate.AllowsSensitiveActions;
        PromLightOwnerSelector.IsEnabled = identityAllowsEnable && !busy;
        PromLightLampSelector.IsEnabled = identityAllowsEnable && !busy;
        PromLightDeviceSelector.IsEnabled = identityAllowsEnable && !busy;
        PromLightNameInput.IsEnabled = identityAllowsEnable && !busy;
        PromLightBindButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightImportButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightRenameButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightDefaultButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightUnbindButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightDetectButton.IsEnabled = identityAllowsEnable && !busy;
        PromLightAssociateCurrentButton.IsEnabled = identityAllowsEnable
            && deviceReady
            && _runtime.Snapshot.State == BridgeRuntimeState.Running
            && !busy;
        PromLightIdleButton.IsEnabled = identityAllowsEnable && deviceReady && !busy;
        PromLightRunningButton.IsEnabled = identityAllowsEnable && deviceReady && !busy;
        PromLightHumanGateButton.IsEnabled = identityAllowsEnable && deviceReady && !busy;
        PromLightErrorButton.IsEnabled = identityAllowsEnable && deviceReady && !busy;
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
            FeishuEventRuntimeState.Faulted => $"连接失败：{snapshot.Error ?? "未取得安全诊断"}",
            _ => "未连接",
        };

        FeishuEventCountText.Text = snapshot.ReceivedEventCount == 0
            ? $"消费者 {snapshot.RegisteredConsumerCount}/{snapshot.ExpectedConsumerCount}；尚未收到测试消息，只统计数量。"
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

        FeishuReplyStatusText.Text = snapshot.ReadOnlyRouteError is not null
            ? $"△ Task 反馈：{snapshot.ReadOnlyRouteError}"
            : snapshot.LastReadOnlyRouteReplyAt is DateTimeOffset taskReplyAt
                ? $"✓ 最近 Task 结果已发送：{taskReplyAt:HH:mm:ss}（文字或附件）。"
            : snapshot.ReplyError is not null
            ? $"✗ 最近自动回复失败：{snapshot.ReplyError}"
            : snapshot.LastReplyAt is DateTimeOffset replyAt
                ? $"✓ 最近自动回复成功：{replyAt:HH:mm:ss}；累计 {snapshot.ReplySentCount} 条。"
                : "○ 尚无自动回复结果。";

    }

    private void RenderGettingStarted()
    {
        FeishuStoredCredential credential = _feishuCredentials.Load();
        FeishuOwnerBindingState owner = _feishuCredentials.LoadOwnerBinding();
        FeishuProjectAuthorizationState project = _feishuCredentials.LoadProjectAuthorization();
        if (!_gettingStartedRendered)
        {
            GettingStartedExpander.IsExpanded = !(credential.HasVerifiedIdentity && owner.IsBound && project.IsAuthorized);
            _gettingStartedRendered = true;
        }

        BotSetupStatusText.Text = credential.HasSecret && credential.HasVerifiedIdentity
            ? "✓ 飞书机器人身份已验证，可以继续或重新检查首次连接。"
            : "创建企业自建应用并启用机器人能力，然后取得 App ID 和 App Secret。";
        FirstConnectionStatusText.Text = credential.HasSecret
            && credential.HasVerifiedIdentity
            && owner.IsBound
            && project.IsAuthorized
                ? "✓ 首次连接已完成；可进入向导检查或更换配置。"
                : "在 Bridge 中安全验证凭据，再选择 Codex 自动配置或四步手动配置。";
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
