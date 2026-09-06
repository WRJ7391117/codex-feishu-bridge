using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record FeishuTestSidecarSession(Process Process, Task Ready);

internal sealed record FeishuEventRuntimeTestHooks(
    Func<string, CancellationToken, Task<FeishuRuntimeCredential?>> LoadRuntimeCredentialAsync,
    Func<FeishuRuntimeCredential, FeishuTestSidecarSession> StartSidecar,
    Func<string, string, CancellationToken, Task<FeishuVerificationResult>> VerifyAndSaveAsync,
    Func<FeishuRuntimeCredential, bool> MatchesActiveGeneration);

public sealed partial class FeishuEventRuntime : IAsyncDisposable
{
    private const string MessageEventKey = "im.message.receive_v1";
    private const string ApprovedTriggerText = "Windows Bridge 回执测试";
    private const string ApprovedReplyText = "DeepOri Bridge for Windows 已收到测试消息。";
    private const string UnmatchedOwnerHelpText = "DeepOri Bridge for Windows 已收到消息，但未识别为支持的指令。请发送“Windows Bridge 回执测试”，或使用“Windows Codex：<问题>”。";
    private const string OwnerBindingReplyText = "DeepOri Bridge for Windows 已完成使用者绑定。后续只读请求无需在电脑上逐条确认。";
    private const string CodexRouteTriggerText = "Windows Codex 路由测试";
    private const string CodexRouteReplyText = "DeepOri Bridge for Windows 已完成 Codex 路由测试。";
    private const string ReadOnlyRoutePrefix = "Windows Codex：";
    private const int ReadOnlyReplyMaxLength = 4000;
    private const string TruncatedReplySuffix = "\n\n[输出已截断]";

    private readonly FeishuCredentialService _credentials;
    private readonly BridgeProductIdentity? _productIdentity;
    private readonly BridgeRuntime? _bridgeRuntime;
    private readonly IPromLightService? _promLight;
    private readonly FeishuEventRuntimeTestHooks? _testHooks;
    private readonly FeishuIdentityTransactionCoordinator _identityTransactions = new();
    private readonly SemaphoreSlim _consumerInputLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FeishuSidecarCommandResult>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, byte> _processedMenuEventIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inputAcceptanceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _activeQueueInputs = new(StringComparer.Ordinal);
    private DateTimeOffset _lastInputSweep;
    private readonly ConcurrentDictionary<string, string> _queueCardStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _queueOwnedCards = new(StringComparer.Ordinal);
    private FeishuTaskInputQueue? _taskInputQueue;
    private FeishuTaskInputQueue TaskInputQueue => LazyInitializer.EnsureInitialized(ref _taskInputQueue,
        () => new FeishuTaskInputQueue(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated", "TaskInputQueue")));
    private FeishuTaskResultOutbox? _taskResultOutbox;
    private FeishuResultResources? _resultResources;
    private FeishuDesktopSyncSubscriptionStore? _desktopSyncSubscriptions;
    private FeishuResultResources ResultResources => LazyInitializer.EnsureInitialized(ref _resultResources,
        () => new FeishuResultResources(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated",
            "TaskResultResources")));
    private CancellationTokenSource? _taskResultWorker;
    private readonly ConcurrentDictionary<string, string> _bridgeTurnIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _observedCurrentReplies = new(StringComparer.Ordinal);
    private FeishuReplyCursorStore? _replyCursors;
    private string[] _replyObservedTaskIds = [];
    private readonly ConcurrentDictionary<string, bool> _replyApprovalWaiting = new(StringComparer.Ordinal);
    private FeishuReplyCursorStore ReplyCursors => LazyInitializer.EnsureInitialized(ref _replyCursors,
        () => new FeishuReplyCursorStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated", "ReplyCursors")));
    private void SaveReplyCursor(string scope, string turnId)
    {
        ReplyCursors.Save(scope, turnId);
        _observedCurrentReplies[scope] = turnId;
    }
    private readonly CodexRolloutTaskStateReader _currentReplyReader = new();
    private FeishuTaskResultOutbox TaskResultOutbox => LazyInitializer.EnsureInitialized(ref _taskResultOutbox,
        () => new FeishuTaskResultOutbox(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated",
            "TaskResultOutbox")));
    private FeishuDesktopSyncSubscriptionStore DesktopSyncSubscriptions => LazyInitializer.EnsureInitialized(
        ref _desktopSyncSubscriptions,
        () => new FeishuDesktopSyncSubscriptionStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated",
            "DesktopSyncSubscriptions")));
    private readonly ConcurrentDictionary<string, MenuCardAction> _menuCardActions = new(StringComparer.Ordinal);
    private sealed record PendingNewTaskProject(string ProjectId, string AuthorizedRoot);
    private readonly ConcurrentDictionary<string, PendingNewTaskProject> _pendingNewTaskProjects = new(StringComparer.Ordinal);
    private FeishuTaskSubscriptionStore? _taskSubscriptions;
    private FeishuTaskSubscriptionStore TaskSubscriptions => LazyInitializer.EnsureInitialized(ref _taskSubscriptions,
        () => new FeishuTaskSubscriptionStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepOriBridge",
            _productIdentity?.StorageChannel ?? "Development-Unsigned-Isolated", "TaskSubscriptions")));
    private string SubscriptionScope(string sender) => (_sidecarAttestation?.IdentityGeneration
        ?? throw new InvalidOperationException("飞书身份世代不可用。")) + ":" + sender + ":" + (_credentials.CurrentTaskSelectionScope(sender)
        ?? throw new InvalidOperationException("该使用者的项目授权暂不可用；订阅已保留。"));
    private readonly SemaphoreSlim _menuConfigurationLock = new(1, 1);
    private readonly FeishuRouteGateCoordinator _routeGates = new();
    private readonly FeishuEventSnapshotStore _snapshotStore;
    private Process? _consumer;
    private SidecarRuntimeAttestation? _sidecarAttestation;
    private TaskCompletionSource _ready = NewReadySource();
    private bool _stopRequested;
    private int _replyArmed;
    private readonly FeishuOwnerBindingGate _ownerBindingGate = new();
    private int _disposeStarted;
    private readonly FeishuArmExpiryScheduler _armExpiryScheduler = new();
    private CancellationTokenSource? _ownerBindingExpiry;

    // Feishu cards carry only an opaque, one-use capability.  The action,
    // bound owner, target Task, and expiry stay in this process; a forged or
    // stale callback therefore cannot choose a task by supplying its own id.
    private sealed record MenuCardAction(
        string SenderId,
        string Action,
        string? TaskId,
        string? ProjectFilter,
        DateTimeOffset ExpiresAt,
        string? AuthorizedProjectRoot,
        string? LampId = null, long? LampRevision = null, string Generation = "");

    private sealed record SidecarRuntimeAttestation(
        string RuntimeNonce,
        string AppId,
        string IdentityFingerprint,
        string IdentityGeneration,
        int ParentProcessId,
        long ParentStartTimeUtcTicks,
        string ParentExecutableSha256,
        string ParentPayloadManifestSha256,
        string SidecarExecutableSha256,
        string SidecarCanonicalPath,
        int SidecarProcessId,
        long SidecarStartTimeUtcTicks);

    private sealed record SidecarManifest(
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("sha256")] string Sha256);

    internal FeishuEventRuntime(
        FeishuCredentialService credentials,
        BridgeProductIdentity productIdentity,
        BridgeRuntime bridgeRuntime)
        : this(credentials, productIdentity, null, bridgeRuntime, null)
    {
    }

    internal FeishuEventRuntime(
        FeishuCredentialService credentials,
        BridgeProductIdentity productIdentity,
        BridgeRuntime bridgeRuntime,
        IPromLightService promLight)
        : this(credentials, productIdentity, null, bridgeRuntime, promLight)
    {
    }

    internal FeishuEventRuntime(
        FeishuCredentialService credentials,
        FeishuEventRuntimeTestHooks testHooks)
        : this(credentials, null, testHooks, null, null)
    {
    }

    private FeishuEventRuntime(
        FeishuCredentialService credentials,
        BridgeProductIdentity? productIdentity,
        FeishuEventRuntimeTestHooks? testHooks,
        BridgeRuntime? bridgeRuntime,
        IPromLightService? promLight)
    {
        _credentials = credentials;
        _productIdentity = productIdentity;
        _testHooks = testHooks;
        _bridgeRuntime = bridgeRuntime;
        _promLight = promLight;
        var initial = new FeishuEventRuntimeSnapshot(
            FeishuEventRuntimeState.Stopped,
            OwnerBound: _credentials.LoadOwnerBinding().IsBound);
        _routeGates.BeginSession(initial.OwnerBound);
        _snapshotStore = new FeishuEventSnapshotStore(
            initial,
            snapshot => ApplyRouteGateState(snapshot, _routeGates.State));
    }

    public event Action<FeishuEventRuntimeSnapshot>? StateChanged;

    public FeishuEventRuntimeSnapshot Snapshot => _snapshotStore.Read();
    public bool CanStartSensitiveRuntime =>
        _testHooks is not null
        || (_productIdentity?.CanUseSensitiveRuntime == true
            && (!_productIdentity.IsPortableIdentity || _credentials.ResourcePrefix == PortablePreview.CredentialPrefix)
            && _credentials.IsSensitiveRuntimeNamespaceAllowed(
                _productIdentity.IsDevelopmentIdentity));

    public void ArmApprovedTestReply()
    {
        FeishuEventRuntimeSnapshot current = Snapshot;
        if (current.State != FeishuEventRuntimeState.Listening)
        {
            throw new InvalidOperationException("请先启动飞书消息长连接。");
        }

        if (!current.OwnerBound)
        {
            throw new InvalidOperationException("请先绑定 owner-only 白名单。");
        }

        Interlocked.Exchange(ref _replyArmed, 1);
        UpdateSnapshot(snapshot => snapshot with { ReplyArmed = true, ReplyError = null });
    }

    public void ArmOwnerBinding()
    {
        FeishuEventRuntimeSnapshot current = Snapshot;
        if (current.State != FeishuEventRuntimeState.Listening)
        {
            throw new InvalidOperationException("请先启动飞书消息长连接。");
        }

        string code = FeishuOwnerBindingCodePolicy.Generate();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        FeishuOwnerBindingGateTicket ticket = _ownerBindingGate.Arm(code, expiresAt);
        _ownerBindingExpiry?.Cancel();
        _ownerBindingExpiry?.Dispose();
        _ownerBindingExpiry = new CancellationTokenSource();
        UpdateSnapshot(snapshot => snapshot with
        {
            OwnerBindingArmed = true,
            OwnerBindingError = null,
            OwnerBindingCode = code,
            OwnerBindingExpiresAt = expiresAt,
            OwnerBindingGeneration = ticket.Generation,
        });
        _ = ExpireOwnerBindingAsync(ticket, _ownerBindingExpiry.Token);
    }

    public void ClearOwnerBinding()
    {
        _armExpiryScheduler.CancelAll();
        _ownerBindingExpiry?.Cancel();
        Interlocked.Exchange(ref _replyArmed, 0);
        _ownerBindingGate.Clear(_credentials.ClearOwnerBinding);
        _routeGates.Invalidate(
            ownerBound: false,
            listening: Snapshot.State == FeishuEventRuntimeState.Listening);
        UpdateSnapshot(snapshot => snapshot with
        {
            OwnerBound = false,
            ReplyArmed = false,
            OwnerBindingArmed = false,
            OwnerBindingError = null,
            OwnerBindingCode = null,
            OwnerBindingExpiresAt = null,
            OwnerBindingGeneration = null,
        });
    }

    public void RefreshOwnerBindingState()
    {
        FeishuOwnerBindingState owner = _credentials.LoadOwnerBinding();
        _routeGates.SetOwnerBound(owner.IsBound);
        UpdateSnapshot(snapshot => snapshot with
        {
            OwnerBound = owner.IsBound,
            OwnerBindingError = owner.Error,
        });
    }

    public void ArmCodexRouteCapture()
    {
        EnsureProjectAuthorized();
        FeishuRouteGateState state = _routeGates.Arm(FeishuRouteKind.Controlled);
        ScheduleArmExpiryRefresh(FeishuRouteKind.Controlled, state);
        UpdateSnapshot(snapshot => snapshot with
        {
            CodexRouteError = null,
        });
    }

    public void ClearCodexRouteCapture()
    {
        _armExpiryScheduler.Cancel(FeishuRouteKind.Controlled);
        _routeGates.Clear(FeishuRouteKind.Controlled);
        UpdateSnapshot(snapshot => snapshot with
        {
            CodexRouteError = null,
        });
    }

    internal FeishuRouteAuthorization ClaimCodexRouteExecution() =>
        ClaimRouteExecution(FeishuRouteKind.Controlled);

    internal async Task ReplyToCapturedCodexRouteAsync(
        FeishuRouteAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        if (authorization.Kind != FeishuRouteKind.Controlled)
        {
            throw new InvalidOperationException("路由授权票据类型不匹配。");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                authorization.SessionCancellation);
            await ReplyToMessageAsync(
                authorization.MessageId,
                CodexRouteReplyText,
                "deepori-win-codex-route",
                linked.Token);
            _routeGates.Complete(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                CodexRouteReplySentCount = snapshot.CodexRouteReplySentCount + 1,
                LastCodexRouteReplyAt = DateTimeOffset.Now,
                CodexRouteError = null,
            });
        }
        catch (Exception exception)
        {
            _routeGates.Abort(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                CodexRouteError = exception is InvalidOperationException
                    ? exception.Message
                    : $"Codex 路由回执失败：{exception.GetType().Name}",
            });
            throw;
        }
    }

    public void ArmReadOnlyRouteCapture()
    {
        EnsureProjectAuthorized();
        FeishuRouteGateState state = _routeGates.Arm(FeishuRouteKind.ReadOnly);
        ScheduleArmExpiryRefresh(FeishuRouteKind.ReadOnly, state);
        UpdateSnapshot(snapshot => snapshot with
        {
            ReadOnlyRouteError = null,
        });
    }

    public void ClearReadOnlyRouteCapture()
    {
        _armExpiryScheduler.Cancel(FeishuRouteKind.ReadOnly);
        _routeGates.Clear(FeishuRouteKind.ReadOnly);
        UpdateSnapshot(snapshot => snapshot with
        {
            ReadOnlyRouteError = null,
        });
    }

    internal FeishuRouteAuthorization ClaimReadOnlyRouteExecution() =>
        ClaimRouteExecution(FeishuRouteKind.ReadOnly);

    internal async Task ReplyToCapturedReadOnlyRouteAsync(
        FeishuRouteAuthorization authorization,
        string agentText,
        CancellationToken cancellationToken = default)
    {
        if (authorization.Kind != FeishuRouteKind.ReadOnly)
        {
            throw new InvalidOperationException("只读路由授权票据类型不匹配。");
        }

        string replyText = agentText?.Trim()
            ?? throw new ArgumentNullException(nameof(agentText));
        if (replyText.Length == 0)
        {
            throw new InvalidOperationException("Codex 没有返回可发送的文本；未发送飞书回复。");
        }

        if (replyText.Length > ReadOnlyReplyMaxLength)
        {
            replyText = replyText[..(ReadOnlyReplyMaxLength - TruncatedReplySuffix.Length)]
                + TruncatedReplySuffix;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                authorization.SessionCancellation);
            await ReplyToMessageAsync(
                authorization.MessageId,
                replyText,
                "deepori-win-readonly-route",
                linked.Token);
            _routeGates.Complete(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteReplySentCount = snapshot.ReadOnlyRouteReplySentCount + 1,
                LastReadOnlyRouteReplyAt = DateTimeOffset.Now,
                ReadOnlyRouteError = null,
                ReadOnlyRouteExecuting = false,
            });
        }
        catch (Exception exception)
        {
            _routeGates.Abort(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteError = exception is InvalidOperationException
                    ? exception.Message
                    : $"只读路由回执失败：{exception.GetType().Name}",
                ReadOnlyRouteExecuting = false,
            });
            throw;
        }
    }

    internal void AbortRouteExecution(FeishuRouteAuthorization authorization)
    {
        _routeGates.Abort(authorization);
        UpdateSnapshot(snapshot => snapshot);
    }

    public async Task StartAsync(
        string intendedAppId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (!CanStartSensitiveRuntime)
        {
            throw new InvalidOperationException(
                "当前构建没有启用受控的本机预览运行时；飞书 sidecar 保持停止。" );
        }
        long startVersion = _identityTransactions.CaptureStartVersion();
        await _identityTransactions.WaitAsync(cancellationToken);
        CancellationTokenSource? attempt = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            FeishuEventRuntimeSnapshot current = Snapshot;
            if (current.State is FeishuEventRuntimeState.Configuring
                or FeishuEventRuntimeState.Connecting
                or FeishuEventRuntimeState.Listening)
            {
                return;
            }

            attempt = _identityTransactions.BeginStart(cancellationToken, startVersion);
            attempt.Token.ThrowIfCancellationRequested();
            SetSnapshot(Snapshot with
            {
                State = FeishuEventRuntimeState.Configuring,
                Error = null,
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            });
            FeishuRuntimeCredential? runtimeCredential = _testHooks is null
                ? await _credentials.LoadForRuntimeAsync(intendedAppId, attempt.Token)
                : await _testHooks.LoadRuntimeCredentialAsync(intendedAppId, attempt.Token);
            if (runtimeCredential is null)
            {
                FeishuStoredCredential stored = _credentials.Load();
                throw new InvalidOperationException(stored.HasSecret && !stored.HasVerifiedIdentity
                    ? "应用身份不匹配或缺少已验证指纹；禁止启动长连接，请重新验证 App ID 与 Secret。"
                    : "请先保存并验证飞书测试机器人凭证。");
            }

            if (!MatchesActiveGeneration(runtimeCredential))
            {
                throw new InvalidOperationException(
                    "飞书身份世代在 sidecar 启动前已变化；连接保持停止。" );
            }

            _stopRequested = false;
            _armExpiryScheduler.CancelAll();
            _ready = NewReadySource();
            Interlocked.Exchange(ref _replyArmed, 0);
            _ownerBindingGate.Clear();
            bool ownerBound = _credentials.LoadOwnerBinding().IsBound;
            _routeGates.BeginSession(ownerBound);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Connecting,
                OwnerBound: ownerBound), _routeGates.State));
            if (_testHooks is null)
            {
                StartEventSidecar(runtimeCredential);
            }
            else
            {
                StartTestSidecar(runtimeCredential, attempt.Token);
            }

            await FeishuRuntimeStartPolicy.WaitForReadyAsync(
                _ready.Task,
                attempt.Token,
                TimeSpan.FromSeconds(30));


            if (!MatchesActiveGeneration(runtimeCredential))
            {
                throw new InvalidOperationException(
                    "飞书身份世代在 sidecar ready 后已变化；连接已拒绝。" );
            }
        }
        catch (OperationCanceledException)
        {
            await StopConsumerOnlyAsync();
            _routeGates.Invalidate(
                _credentials.LoadOwnerBinding().IsBound,
                listening: false);
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Stopped,
                Error = "连接准备已取消；sidecar 已停止。",
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            });
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StopConsumerOnlyAsync();
            _routeGates.Invalidate(
                _credentials.LoadOwnerBinding().IsBound,
                listening: false);
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Faulted,
                Error = SafeError(exception),
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            });
            throw;
        }
        finally
        {
            _identityTransactions.EndStart(attempt);
            _identityTransactions.Release();
        }
    }

    public async Task OnIntendedIdentityChangedAsync(
        string intendedAppId,
        CancellationToken cancellationToken = default)
    {
        await _identityTransactions.CancelStartAndWaitDurablyAsync();
        try
        {
            FeishuStoredCredential stored = _credentials.Load();
            if (!FeishuIdentityGenerationPolicy.RequiresAtomicInvalidation(
                    Snapshot.State,
                    intendedAppId,
                    stored))
            {
                return;
            }

            _stopRequested = true;
            await StopConsumerOnlyAsync();
            _ownerBindingGate.Clear(_credentials.ClearOwnerBinding);
            _routeGates.Invalidate(ownerBound: false, listening: false);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Stopped,
                Error: "预期 App 身份已改变；旧授权世代与所有内存票据已清除。",
                OwnerBound: false), _routeGates.State));
        }
        finally
        {
            _identityTransactions.Release();
        }
    }

    public async Task PrepareIdentityVerificationAsync(
        CancellationToken cancellationToken = default)
    {
        await _identityTransactions.CancelStartAndWaitDurablyAsync();
        try
        {
            _stopRequested = true;
            _armExpiryScheduler.CancelAll();
            Interlocked.Exchange(ref _replyArmed, 0);
            _ownerBindingGate.Clear();
            await StopConsumerOnlyAsync();
            _routeGates.Invalidate(
                _credentials.LoadOwnerBinding().IsBound,
                listening: false);
            UpdateSnapshot(snapshot => ApplyRouteGateState(snapshot with
            {
                State = FeishuEventRuntimeState.Stopped,
                Error = null,
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            }, _routeGates.State));
        }
        finally
        {
            _identityTransactions.Release();
        }
    }

    public async Task<FeishuVerificationResult> VerifyAndSaveIdentityAsync(
        string appId,
        string suppliedSecret,
        CancellationToken cancellationToken = default)
    {
        // Verify the draft while the prior verified generation remains active. A failed
        // or cancelled draft must not tear down its owner/project/runtime state.
        FeishuVerificationResult result = _testHooks is null
            ? await _credentials.VerifyAndSaveAsync(appId, suppliedSecret, cancellationToken)
            : await _testHooks.VerifyAndSaveAsync(appId, suppliedSecret, cancellationToken);
        bool credentialWriteSucceeded = result.CredentialWriteSucceeded
            || (_testHooks is not null && result.Success);
        if (!credentialWriteSucceeded)
        {
            return result;
        }

        // Once Credential Locker has accepted a new generation, cleanup is durable and
        // no cancellation path may pretend that the old generation is still current.
        await _identityTransactions.CancelStartAndWaitDurablyAsync();
        try
        {
            _stopRequested = true;
            _armExpiryScheduler.CancelAll();
            Interlocked.Exchange(ref _replyArmed, 0);
            _ownerBindingGate.Clear();
            await StopConsumerOnlyAsync();
            bool currentlyVerified = _testHooks is not null
                ? result.Success
                : FirstConnectionNavigationGate.IsCurrentlyVerified(_credentials.Load());
            bool ownerBound = currentlyVerified && _credentials.LoadOwnerBinding().IsBound;
            _routeGates.Invalidate(ownerBound, listening: false);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Stopped,
                Error: currentlyVerified ? null : result.Message,
                OwnerBound: ownerBound), _routeGates.State));
            return result;
        }
        catch
        {
            await StopConsumerOnlyAsync();
            _routeGates.Invalidate(ownerBound: false, listening: false);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Stopped,
                Error: "身份验证未完成；连接保持停止。",
                OwnerBound: false), _routeGates.State));
            throw;
        }
        finally
        {
            _identityTransactions.Release();
        }
    }

    public async Task ClearIdentityAsync(CancellationToken cancellationToken = default)
    {
        await _identityTransactions.CancelStartAndWaitDurablyAsync();
        try
        {
            _stopRequested = true;
            _armExpiryScheduler.CancelAll();
            Interlocked.Exchange(ref _replyArmed, 0);
            _ownerBindingGate.Clear();
            await StopConsumerOnlyAsync();
            _credentials.Clear();
            _routeGates.Invalidate(ownerBound: false, listening: false);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Stopped,
                OwnerBound: false), _routeGates.State));
        }
        finally
        {
            _identityTransactions.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _identityTransactions.CancelStartAndWaitDurablyAsync();
        try
        {
            FeishuEventRuntimeSnapshot current = Snapshot;
            if (current.State == FeishuEventRuntimeState.Stopped)
            {
                return;
            }

            _stopRequested = true;
            _armExpiryScheduler.CancelAll();
            Interlocked.Exchange(ref _replyArmed, 0);
            _ownerBindingGate.Clear();
            _routeGates.Invalidate(
                _credentials.LoadOwnerBinding().IsBound,
                listening: false);
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Stopping,
                Error = null,
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            });
            await StopConsumerOnlyAsync();
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Stopped,
                Error = null,
                OwnerBound = _credentials.LoadOwnerBinding().IsBound,
            });
        }
        finally
        {
            _identityTransactions.Release();
        }
    }

    public async Task ConfirmAndPublishMenuAsync(
        bool explicitHumanConfirmation,
        CancellationToken cancellationToken = default)
    {
        if (!explicitHumanConfirmation)
        {
            throw new InvalidOperationException("提交飞书应用版本前必须取得明确的人类确认。");
        }

        FeishuMenuConfigurationStage stage = Snapshot.MenuConfigurationStage;
        if (stage is not (FeishuMenuConfigurationStage.WrittenPendingPublish
            or FeishuMenuConfigurationStage.PublishRejected))
        {
            throw new InvalidOperationException("当前没有经过身份核验且等待发布的 Bot 菜单候选。");
        }

        await _menuConfigurationLock.WaitAsync(cancellationToken);
        try
        {
            SidecarRuntimeAttestation attestation = RequireActiveSidecarAttestation();
            UpdateSnapshot(snapshot => snapshot with
            {
                MenuConfigurationStage = FeishuMenuConfigurationStage.Publishing,
                MenuConfigurationBusy = true,
                MenuPublishConfirmationRequired = false,
                MenuConfigurationDetail = "正在重新核验 Bot 身份世代并提交受管菜单版本…",
            });
            FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
                FeishuSidecarProtocol.BuildMenuCommandJson(
                    FeishuSidecarProtocol.MenuPublishCommandType,
                    NewRequestId(),
                    attestation.IdentityGeneration),
                cancellationToken);
            ApplyMenuCommandResult(result);
            if (Snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.SubmittedPendingReview)
            {
                _ = PollMenuStatusAsync(_routeGates.SessionCancellation);
            }
        }
        catch (Exception exception)
        {
            SetMenuFailure(exception);
            throw;
        }
        finally
        {
            _menuConfigurationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _replyArmed, 0);
        _ownerBindingGate.Clear();
        _ownerBindingExpiry?.Cancel();
        _routeGates.Invalidate(
            _credentials.LoadOwnerBinding().IsBound,
            listening: false);
        UpdateSnapshot(snapshot => snapshot with
        {
            ReplyArmed = false,
            OwnerBindingArmed = false,
            OwnerBindingCode = null,
            OwnerBindingExpiresAt = null,
            OwnerBindingGeneration = null,
        });
        _armExpiryScheduler.CancelAll();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await StopAsync(timeout.Token);
        }
        catch
        {
            await StopConsumerOnlyAsync();
        }

        await _armExpiryScheduler.DisposeAsync();
        _ownerBindingExpiry?.Dispose();
        _routeGates.Dispose();
    }

    private async Task ExpireOwnerBindingAsync(
        FeishuOwnerBindingGateTicket ticket,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
            if (!_ownerBindingGate.TryExpire(ticket, DateTimeOffset.UtcNow))
            {
                return;
            }

            UpdateSnapshot(snapshot => string.Equals(
                    snapshot.OwnerBindingGeneration,
                    ticket.Generation,
                    StringComparison.Ordinal)
                ? snapshot with
                {
                    OwnerBindingArmed = false,
                    OwnerBindingCode = null,
                    OwnerBindingExpiresAt = null,
                    OwnerBindingGeneration = null,
                    OwnerBindingError = "一次性绑定码已过期，请重新生成。",
                }
                : snapshot);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnConsumerOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        ProcessConsumerOutput(sender as Process, eventArgs.Data ?? string.Empty);
    }

    private void StartEventSidecar(FeishuRuntimeCredential credential)
    {
        string sidecarPath = Path.Combine(
            AppContext.BaseDirectory,
            "Tools",
            "feishu-event-sidecar.exe");
        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException("飞书官方 SDK sidecar 未随应用部署。", sidecarPath);
        }

        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "Tools",
            "feishu-event-sidecar.manifest.json");
        if (!File.Exists(manifestPath)
            || FileAttestation.HasReparsePoint(sidecarPath, AppContext.BaseDirectory)
            || FileAttestation.HasReparsePoint(manifestPath, AppContext.BaseDirectory))
        {
            throw new InvalidOperationException("飞书 sidecar 或其 manifest 缺失，或路径包含重解析点。" );
        }

        string canonicalSidecar = FileAttestation.CanonicalPath(sidecarPath);
        string expectedCanonical = Path.GetFullPath(sidecarPath);
        if (!canonicalSidecar.Equals(expectedCanonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("飞书 sidecar 规范路径不匹配。" );
        }

        SidecarManifest? manifest = JsonSerializer.Deserialize<SidecarManifest>(
            File.ReadAllText(manifestPath));
        string sidecarSha256 = FileAttestation.Sha256(canonicalSidecar);
        if (manifest is null
            || manifest.ProtocolVersion != 1
            || !manifest.FileName.Equals("feishu-event-sidecar.exe", StringComparison.Ordinal)
            || !FileAttestation.FixedTimeEquals(
                manifest.Sha256,
                FeishuSidecarBuildAttestation.ExpectedSha256)
            || !FileAttestation.FixedTimeEquals(manifest.Sha256, sidecarSha256))
        {
            throw new InvalidOperationException("飞书 sidecar manifest 哈希认证失败。" );
        }

        BridgeCandidateAttestation parent = (_productIdentity
            ?? throw new InvalidOperationException("产品身份认证服务不可用。"))
            .GetBoundInstance();
        string runtimeNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var utf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var startInfo = new ProcessStartInfo
        {
            FileName = sidecarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        var consumer = new Process { StartInfo = startInfo };
        consumer.EnableRaisingEvents = true;
        consumer.OutputDataReceived += OnConsumerOutput;
        consumer.ErrorDataReceived += OnConsumerDiagnostic;
        consumer.Exited += OnConsumerExited;

        if (!consumer.Start())
        {
            consumer.Dispose();
            throw new InvalidOperationException("飞书官方 SDK sidecar 未能启动。");
        }

        string childPath = FileAttestation.CanonicalPath(
            FileAttestation.ProcessImagePath(consumer));
        if (!childPath.Equals(canonicalSidecar, StringComparison.OrdinalIgnoreCase))
        {
            consumer.Kill(entireProcessTree: true);
            consumer.Dispose();
            throw new InvalidOperationException("启动后的飞书 sidecar 进程路径发生变化。" );
        }

        _sidecarAttestation = new SidecarRuntimeAttestation(
            runtimeNonce,
            credential.AppId,
            credential.IdentityFingerprint,
            credential.IdentityGeneration,
            parent.ProcessId,
            parent.ProcessStartTimeUtcTicks,
            parent.ExecutableSha256,
            parent.PayloadManifestSha256,
            sidecarSha256,
            canonicalSidecar,
            consumer.Id,
            consumer.StartTime.ToUniversalTime().Ticks);

        Interlocked.Exchange(ref _consumer, consumer);
        consumer.BeginOutputReadLine();
        consumer.BeginErrorReadLine();
        string startupJson = JsonSerializer.Serialize(new
        {
            app_id = credential.AppId,
            app_secret = credential.Secret,
            runtime_nonce = runtimeNonce,
            identity_fingerprint = credential.IdentityFingerprint,
            identity_generation = credential.IdentityGeneration,
            parent_pid = parent.ProcessId,
            parent_start_time_utc_ticks = parent.ProcessStartTimeUtcTicks,
            parent_executable_sha256 = parent.ExecutableSha256,
            parent_payload_manifest_sha256 = parent.PayloadManifestSha256,
            sidecar_executable_sha256 = sidecarSha256,
        });
        consumer.StandardInput.WriteLine(startupJson);
        consumer.StandardInput.Flush();
    }

    private void StartTestSidecar(
        FeishuRuntimeCredential credential,
        CancellationToken attemptToken)
    {
        FeishuTestSidecarSession session = (_testHooks
            ?? throw new InvalidOperationException("测试 sidecar hook 不可用。"))
            .StartSidecar(credential);
        if (session.Process.HasExited)
        {
            session.Process.Dispose();
            throw new InvalidOperationException("测试 sidecar 未保持运行。" );
        }

        Interlocked.Exchange(ref _consumer, session.Process);
        _ = ObserveTestReadyAsync(session, credential, attemptToken);
    }

    private async Task ObserveTestReadyAsync(
        FeishuTestSidecarSession session,
        FeishuRuntimeCredential credential,
        CancellationToken attemptToken)
    {
        try
        {
            await session.Ready;
            if (!ReferenceEquals(Volatile.Read(ref _consumer), session.Process)
                || attemptToken.IsCancellationRequested
                || session.Process.HasExited
                || !MatchesActiveGeneration(credential))
            {
                return;
            }

            _routeGates.SetListening(true);
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Listening,
                Error = null,
                RegisteredConsumerCount = FeishuSidecarProtocol.ExpectedConsumers.Length,
                ExpectedConsumerCount = FeishuSidecarProtocol.ExpectedConsumers.Length,
            });
            _ready.TrySetResult();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Volatile.Read(ref _consumer), session.Process)
                && !attemptToken.IsCancellationRequested)
            {
                _ready.TrySetException(exception);
            }
        }
    }

    private bool MatchesActiveGeneration(FeishuRuntimeCredential credential) =>
        _testHooks?.MatchesActiveGeneration(credential)
        ?? _credentials.MatchesActiveGeneration(credential);

    private void ProcessConsumerOutput(Process? source, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (source is null || !ReferenceEquals(Volatile.Read(ref _consumer), source))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (FeishuSidecarProtocol.TryReadCommandResult(
                root,
                out string requestId,
                out FeishuSidecarCommandResult result))
            {
                if (_pendingCommands.TryRemove(
                    requestId,
                    out TaskCompletionSource<FeishuSidecarCommandResult>? completion))
                {
                    completion.TrySetResult(result);
                }

                return;
            }

            string? eventType = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;
            if (string.Equals(eventType, "lifecycle", StringComparison.Ordinal))
            {
                string? lifecycleState = root.TryGetProperty(
                    "state",
                    out JsonElement stateElement)
                        ? stateElement.GetString()
                        : null;
                if (string.Equals(lifecycleState, "ready", StringComparison.Ordinal))
                {
                    if (!ValidateReadyAttestation(source, root))
                    {
                        _routeGates.SetListening(false);
                        _ready.TrySetException(new InvalidOperationException(
                            "飞书 sidecar ready 身份回显不匹配。"));
                        try
                        {
                            if (!source.HasExited)
                            {
                                source.Kill(entireProcessTree: true);
                            }
                        }
                        catch
                        {
                        }
                        return;
                    }

                    _routeGates.SetListening(true);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Listening,
                        Error = null,
                        RegisteredConsumerCount = FeishuSidecarProtocol.ExpectedConsumers.Length,
                        ExpectedConsumerCount = FeishuSidecarProtocol.ExpectedConsumers.Length,
                    });
                    _ready.TrySetResult();
                    if (_testHooks is null)
                    {
                        StartTaskResultWorker();
                        _ = InspectMenuReadOnlyAsync(_routeGates.SessionCancellation);
                    }
                }
                else if (string.Equals(
                    lifecycleState,
                    "reconnecting",
                    StringComparison.Ordinal))
                {
                    _armExpiryScheduler.CancelAll();
                    _routeGates.SetListening(false);
                    Interlocked.Exchange(ref _replyArmed, 0);
                    _ownerBindingGate.Clear();
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Connecting,
                        Error = null,
                        ReplyArmed = false,
                        OwnerBindingArmed = false,
                        OwnerBindingCode = null,
                        OwnerBindingExpiresAt = null,
                        OwnerBindingGeneration = null,
                    });
                }
                else if (string.Equals(
                    lifecycleState,
                    "disconnected",
                    StringComparison.Ordinal))
                {
                    _armExpiryScheduler.CancelAll();
                    _routeGates.SetListening(false);
                    Interlocked.Exchange(ref _replyArmed, 0);
                    _ownerBindingGate.Clear();
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Connecting,
                        Error = "飞书 WebSocket 已断开；所有人工 Gate 已清除，等待 SDK 重新连接。",
                        ReplyArmed = false,
                        OwnerBindingArmed = false,
                        OwnerBindingCode = null,
                        OwnerBindingExpiresAt = null,
                        OwnerBindingGeneration = null,
                    });
                }

                return;
            }

            if (string.Equals(eventType, "application.bot.menu_v6", StringComparison.Ordinal))
            {
                string eventId = root.TryGetProperty("message_id", out JsonElement menuEventId)
                    && menuEventId.ValueKind == JsonValueKind.String
                        ? menuEventId.GetString() ?? string.Empty
                        : string.Empty;
                string menuSender = root.TryGetProperty("sender_id", out JsonElement menuSenderId)
                    && menuSenderId.ValueKind == JsonValueKind.String
                        ? menuSenderId.GetString() ?? string.Empty
                        : string.Empty;
                string eventKey = root.TryGetProperty("content", out JsonElement menuEventKey)
                    && menuEventKey.ValueKind == JsonValueKind.String
                        ? menuEventKey.GetString() ?? string.Empty
                        : string.Empty;
                UpdateSnapshot(snapshot => snapshot with
                {
                    MenuEventCount = snapshot.MenuEventCount + 1,
                    LastReceivedAt = DateTimeOffset.Now,
                });
                if (eventId.Length == 0
                    || menuSender.Length == 0
                    || eventKey.Length == 0
                    || !_credentials.MatchesBoundOwner(menuSender))
                {
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        MenuBusinessReplyError = "菜单事件缺少完整事件引用、未匹配 owner，或事件键无效；未执行也未回复。",
                    });
                    return;
                }

                if (!_processedMenuEventIds.TryAdd(eventId, 0))
                {
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        MenuBusinessReplyError = null,
                        LastMatchDiagnostic = "最近菜单事件已在本次连接中去重；未重复执行或回复。",
                    });
                    return;
                }

                if (_processedMenuEventIds.Count > 2048)
                {
                    _processedMenuEventIds.Clear();
                    _processedMenuEventIds.TryAdd(eventId, 0);
                }

                _ = ShowMenuCardAsync(eventId, menuSender, eventKey, _routeGates.SessionCancellation, isMenuEvent: true);
                return;
            }

            if (string.Equals(eventType, "card.action.trigger", StringComparison.Ordinal))
            {
                UpdateSnapshot(snapshot => snapshot with
                {
                    CardActionEventCount = snapshot.CardActionEventCount + 1,
                    LastReceivedAt = DateTimeOffset.Now,
                });
                string cardSender = root.TryGetProperty("sender_id", out JsonElement cardSenderId)
                    && cardSenderId.ValueKind == JsonValueKind.String ? cardSenderId.GetString() ?? string.Empty : string.Empty;
                string cardAction = root.TryGetProperty("content", out JsonElement cardContent)
                    && cardContent.ValueKind == JsonValueKind.String ? cardContent.GetString() ?? string.Empty : string.Empty;
                string cardToken = root.TryGetProperty("card_token", out JsonElement cardTokenElement)
                    && cardTokenElement.ValueKind == JsonValueKind.String ? cardTokenElement.GetString() ?? string.Empty : string.Empty;
                string cardEventId = root.TryGetProperty("message_id", out JsonElement cardEventIdElement)
                    && cardEventIdElement.ValueKind == JsonValueKind.String ? cardEventIdElement.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(cardEventId)
                    && _credentials.MatchesBoundOwner(cardSender)
                    && _processedMenuEventIds.TryAdd("card:" + cardEventId, 0))
                {
                    _ = ExecuteMenuCardActionAsync(
                        cardEventId,
                        cardSender,
                        cardAction,
                        cardToken,
                        _routeGates.SessionCancellation);
                }
                return;
            }

            if (!string.Equals(
                eventType,
                MessageEventKey,
                StringComparison.Ordinal))
            {
                return;
            }

            DateTimeOffset eventReceivedAt = DateTimeOffset.UtcNow;
            UpdateSnapshot(snapshot => snapshot with
            {
                ReceivedEventCount = snapshot.ReceivedEventCount + 1,
                LastReceivedAt = eventReceivedAt,
            });

            string? chatType = root.TryGetProperty("chat_type", out JsonElement chatTypeElement)
                ? chatTypeElement.GetString()
                : null;
            string? senderType = root.TryGetProperty("sender_type", out JsonElement senderTypeElement)
                ? senderTypeElement.GetString()
                : null;
            string? messageType = root.TryGetProperty("message_type", out JsonElement messageTypeElement)
                ? messageTypeElement.GetString()
                : null;
            string? messageId = root.TryGetProperty("message_id", out JsonElement messageIdElement)
                ? messageIdElement.GetString()
                : null;
            string? senderId = root.TryGetProperty("sender_id", out JsonElement senderIdElement)
                ? senderIdElement.GetString()
                : null;
            string? content = root.TryGetProperty("content", out JsonElement contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;
            string contentState = root.TryGetProperty("content_state", out JsonElement contentStateElement)
                && contentStateElement.ValueKind == JsonValueKind.String
                && contentStateElement.GetString() is "text-json-decoded" or "post-text-decoded" or "sdk-normalized"
                    ? contentStateElement.GetString()!
                    : "unknown";
            // Resource messages never enter command/owner-binding parsing. Authenticate
            // their sender first, then process both text and resources as one Task input.
            if (messageType != "text" || root.TryGetProperty("resources", out _)
                || root.TryGetProperty("resource_error", out _))
            {
                if (chatType != "p2p" || senderType != "user"
                    || string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(senderId)
                    || !_credentials.MatchesBoundOwner(senderId)) return;
                FeishuProjectAuthorizationState scope = _credentials.LoadProjectAuthorization(senderId);
                if (!scope.IsAuthorized) return;
                UpdateSnapshot(snapshot => snapshot with
                {
                    LastMatchDiagnostic = "附件事件：已核验单聊、owner 和项目授权；正在准备当前 Task 输入。",
                });
                _ = AcceptTaskInputAsync(messageId, senderId,
                    string.IsNullOrWhiteSpace(content) ? "请查看用户从飞书发送的附件。" : content,
                    scope.ProjectPath, _routeGates.SessionCancellation, root.Clone());
                return;
            }
            if (!FeishuEventSafetyPolicy.TryExtractCanonicalText(
                    messageType,
                    content,
                    out string canonicalText,
                    out string appContentState))
            {
                UpdateSnapshot(snapshot => snapshot with
                {
                    LastMatchDiagnostic = "最近事件安全匹配：非文本、空白或含歧义的正文已拒绝；未执行也未回复。",
                });
                return;
            }
            content = canonicalText;
            contentState = contentState is "text-json-decoded" or "post-text-decoded"
                ? contentState
                : appContentState;
            FeishuEventRuntimeSnapshot bindingSnapshot = Snapshot;
            FeishuOwnerBindingGateTicket? ownerBindingTicket =
                bindingSnapshot.OwnerBindingGeneration is { Length: 32 } generation
                && bindingSnapshot.OwnerBindingCode is { Length: 6 } bindingCode
                && bindingSnapshot.OwnerBindingExpiresAt is DateTimeOffset bindingExpiresAt
                    ? new FeishuOwnerBindingGateTicket(
                        generation,
                        bindingCode,
                        bindingExpiresAt)
                    : null;
            FeishuOwnerBindingMatchResult ownerBindingMatch = FeishuOwnerBindingCodePolicy.Evaluate(
                bindingSnapshot.OwnerBindingCode,
                bindingSnapshot.OwnerBindingExpiresAt,
                content,
                eventReceivedAt);
            bool matchesOwnerBindingCode = bindingSnapshot.OwnerBindingArmed
                && ownerBindingTicket is not null
                && ownerBindingMatch.Matches;
            FeishuEventSafetyPolicy.HarmlessReplyTestAnalysis replyTestAnalysis =
                FeishuEventSafetyPolicy.AnalyzeHarmlessReplyTest(
                    content,
                    ApprovedTriggerText);
            bool pendingNewTaskTitle = senderId is { Length: > 0 }
                && _pendingNewTaskProjects.ContainsKey(senderId);
            string approvedAction =
                pendingNewTaskTitle
                    ? "new-task-title"
                    : replyTestAnalysis.Matched
                    ? "reply-test"
                    : matchesOwnerBindingCode
                        ? "owner-bind"
                        : FeishuEventSafetyPolicy.MatchesFixedCommand(content, CodexRouteTriggerText)
                            ? "codex-route-test"
                        : FeishuEventSafetyPolicy.TryExtractReadOnlyPrompt(content, out _)
                            ? "codex-readonly"
                            : _bridgeRuntime?.SelectedThreadId is { Length: > 0 }
                                ? "codex-current"
                            : "unmatched";
            string? readOnlyPrompt = FeishuEventSafetyPolicy.TryExtractReadOnlyPrompt(
                content,
                out string extractedReadOnlyPrompt)
                    ? extractedReadOnlyPrompt
                    : null;

            bool isP2p = string.Equals(chatType, "p2p", StringComparison.Ordinal);
            bool isUser = string.Equals(senderType, "user", StringComparison.Ordinal);
            bool isText = string.Equals(messageType, "text", StringComparison.Ordinal);
            bool hasMessageId = !string.IsNullOrWhiteSpace(messageId);
            bool hasSenderId = !string.IsNullOrWhiteSpace(senderId);
            bool approvedTarget = FeishuEventSafetyPolicy.IsApprovedTarget(
                chatType,
                senderType,
                messageType,
                hasMessageId,
                hasSenderId);
            bool isOwnerBind = string.Equals(approvedAction, "owner-bind", StringComparison.Ordinal);
            bool ownerBound = _credentials.LoadOwnerBinding().IsBound;
            bool ownerMatches = ownerBound
                && hasSenderId
                && _credentials.MatchesBoundOwner(senderId!);
            FeishuProjectAuthorizationState projectAuthorization = hasSenderId
                ? _credentials.LoadProjectAuthorization(senderId!)
                : new FeishuProjectAuthorizationState(false, string.Empty);
            bool projectAuthorized = projectAuthorization.IsAuthorized;
            bool isReplyTest = string.Equals(approvedAction, "reply-test", StringComparison.Ordinal);
            bool isCodexRouteTest = string.Equals(
                approvedAction,
                "codex-route-test",
                StringComparison.Ordinal);
            bool isReadOnlyRoute = string.Equals(
                approvedAction,
                "codex-readonly",
                StringComparison.Ordinal);
            string ownerBindingDiagnostic = bindingSnapshot.OwnerBindingArmed
                ? $"；owner 码诊断：去首尾空白后长度={ownerBindingMatch.TrimmedLength}；整条为 ASCII 六位数字={YesNo(ownerBindingMatch.IsEntireSixAsciiDigits)}；忽略有效期匹配当前码={YesNo(ownerBindingMatch.MatchesIgnoringExpiry)}；到达时未过期={YesNo(ownerBindingMatch.IsUnexpiredAtArrival)}"
                : string.Empty;

            _routeGates.SetOwnerBound(ownerBound);
            UpdateSnapshot(snapshot => snapshot with
            {
                OwnerBound = ownerBound,
                LastOwnerBindingTrimmedLength = bindingSnapshot.OwnerBindingArmed
                    ? ownerBindingMatch.TrimmedLength
                    : null,
                LastOwnerBindingWasSixAsciiDigits = bindingSnapshot.OwnerBindingArmed
                    ? ownerBindingMatch.IsEntireSixAsciiDigits
                    : null,
                LastOwnerBindingMatchedIgnoringExpiry = bindingSnapshot.OwnerBindingArmed
                    ? ownerBindingMatch.MatchesIgnoringExpiry
                    : null,
                LastOwnerBindingWasUnexpiredAtArrival = bindingSnapshot.OwnerBindingArmed
                    ? ownerBindingMatch.IsUnexpiredAtArrival
                    : null,
                LastMatchDiagnostic = $"最近事件安全匹配：应用动作标签={approvedAction}；正文解包={contentState}；回执测试固定词组={replyTestAnalysis.MatchedTokenCount}/{replyTestAnalysis.ExpectedTokenCount}；单聊={YesNo(isP2p)}；用户={YesNo(isUser)}；文本类型={YesNo(isText)}；消息引用={YesNo(hasMessageId)}；owner={OwnerLabel(ownerBound, ownerMatches)}；明确项目授权={YesNo(projectAuthorized)}{ownerBindingDiagnostic}。",
            });

            if (approvedTarget && ownerMatches && content.StartsWith("灯名：", StringComparison.Ordinal)
                && _pendingLampNames.ContainsKey(senderId!))
            {
                _ = CompleteLampRenameAsync(messageId!, senderId!, content[3..], _routeGates.SessionCancellation);
                return;
            }
            if (pendingNewTaskTitle && approvedTarget && ownerMatches && projectAuthorized)
            {
                if (_pendingNewTaskProjects.TryRemove(senderId!, out PendingNewTaskProject? pendingProject))
                {
                    _ = CompleteNewTaskAsync(
                        messageId!,
                        senderId!,
                        pendingProject,
                        content,
                        _routeGates.SessionCancellation);
                }
                return;
            }

            if (string.Equals(approvedAction, "codex-current", StringComparison.Ordinal)
                && approvedTarget
                && ownerMatches
                && projectAuthorized)
            {
                _ = AcceptTaskInputAsync(
                    messageId!,
                    senderId!,
                    content,
                    projectAuthorization.ProjectPath,
                    _routeGates.SessionCancellation);
                return;
            }

            string? approvedReadOnlyPrompt = readOnlyPrompt?.Trim();
            if (approvedReadOnlyPrompt?.Length > BridgeRuntime.ReadOnlyPromptMaxLength)
            {
                approvedReadOnlyPrompt = null;
            }

            FeishuRouteCaptureResult capture = _routeGates.ProcessMessage(
                messageId ?? string.Empty,
                approvedAction,
                approvedReadOnlyPrompt,
                approvedTarget,
                ownerMatches && projectAuthorized,
                DateTimeOffset.Now,
                projectAuthorization.ProjectPath,
                allowAutomaticReadOnly: _bridgeRuntime is not null);
            if (capture.Duplicate)
            {
                UpdateSnapshot(snapshot => snapshot with
                {
                    LastMatchDiagnostic = "最近事件安全匹配：重复消息引用已在内存去重；未重新捕获、执行或回复。",
                });
                return;
            }

            if (approvedTarget && isOwnerBind && ownerBindingTicket is not null)
            {
                try
                {
                    bool committed = _ownerBindingGate.TryCommit(
                        ownerBindingTicket,
                        eventReceivedAt,
                        DateTimeOffset.UtcNow,
                        () => _credentials.BindOwner(senderId!));
                    if (!committed)
                    {
                        UpdateSnapshot(snapshot => snapshot with
                        {
                            LastMatchDiagnostic = "最近事件安全匹配：owner 码正文曾匹配，但对应 Gate generation 已过期、清除或被替换；未绑定任何用户。",
                        });
                        return;
                    }

                    _ownerBindingExpiry?.Cancel();
                    _routeGates.SetOwnerBound(true);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        OwnerBound = true,
                        OwnerBindingArmed = false,
                        LastOwnerBoundAt = DateTimeOffset.Now,
                        OwnerBindingError = null,
                        OwnerBindingCode = null,
                        OwnerBindingExpiresAt = null,
                        OwnerBindingGeneration = null,
                        LastMatchDiagnostic = $"最近事件安全匹配：CLI 动作标签=owner-bind；单聊=是；用户=是；文本类型=是；消息引用=是；owner=匹配。",
                    });
                    _ = ReplyToAutomaticMessageAsync(
                        messageId!,
                        OwnerBindingReplyText,
                        "deepori-win-owner-binding",
                        _routeGates.SessionCancellation);
                }
                catch (Exception exception)
                {
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        OwnerBindingArmed = false,
                        OwnerBindingError = $"owner 绑定失败：{exception.GetType().Name}",
                        OwnerBindingCode = null,
                        OwnerBindingExpiresAt = null,
                        OwnerBindingGeneration = null,
                    });
                }
            }

            if (approvedTarget
                && isReplyTest
                && ownerMatches)
            {
                Interlocked.Exchange(ref _replyArmed, 0);
                UpdateSnapshot(snapshot => snapshot with { ReplyArmed = false, ReplyError = null });
                _ = ReplyToAutomaticMessageAsync(
                    messageId!,
                    ApprovedReplyText,
                    "deepori-win-test",
                    _routeGates.SessionCancellation);
            }

            if (FeishuEventSafetyPolicy.CanSendUnmatchedOwnerHelp(
                approvedAction,
                approvedTarget,
                ownerMatches,
                capture.ControlledCaptured,
                capture.ReadOnlyCaptured))
            {
                UpdateSnapshot(snapshot => snapshot with { ReplyError = null });
                _ = ReplyToAutomaticMessageAsync(
                    messageId!,
                    UnmatchedOwnerHelpText,
                    "deepori-win-unmatched-help",
                    _routeGates.SessionCancellation);
            }

            if (capture.ControlledCaptured)
            {
                _armExpiryScheduler.Cancel(FeishuRouteKind.Controlled);
                ScheduleAuthorizationExpiry(
                    FeishuRouteKind.Controlled,
                    capture.State);
                UpdateSnapshot(snapshot => snapshot with
                {
                    LastCodexRouteCapturedAt = DateTimeOffset.Now,
                    LastMatchDiagnostic = isCodexRouteTest
                        ? "最近事件安全匹配：应用动作标签=codex-route-test；单聊=是；用户=是；文本类型=是；消息引用=是；owner=匹配。"
                        : "最近事件安全匹配：应用动作标签=codex-route-manual-gate；固定正文未匹配，但单聊、用户、文本类型、消息引用和 owner 均匹配；仅进入本机人工授权 Gate。",
                });
            }

            if (approvedTarget && isReadOnlyRoute && ownerMatches)
            {
                string prompt = readOnlyPrompt?.Trim() ?? string.Empty;
                if (prompt.Length == 0)
                {
                    _routeGates.Clear(FeishuRouteKind.ReadOnly);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        ReadOnlyRouteError = "只读提示词为空；没有捕获，也不会创建 Codex Task。",
                    });
                }
                else if (prompt.Length > BridgeRuntime.ReadOnlyPromptMaxLength)
                {
                    _routeGates.Clear(FeishuRouteKind.ReadOnly);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        ReadOnlyRouteError = $"只读提示词超过 {BridgeRuntime.ReadOnlyPromptMaxLength} 个字符；没有捕获，也不会创建 Codex Task。",
                    });
                }
                else if (capture.ReadOnlyCaptured)
                {
                    _armExpiryScheduler.Cancel(FeishuRouteKind.ReadOnly);
                    ScheduleAuthorizationExpiry(
                        FeishuRouteKind.ReadOnly,
                        capture.State);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        LastReadOnlyRouteCapturedAt = DateTimeOffset.Now,
                        ReadOnlyRouteError = null,
                    });
                    if (_bridgeRuntime is not null)
                    {
                        _ = ExecuteAutomaticReadOnlyRouteAsync();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-event stdout. Raw lines are intentionally not persisted.
        }
        catch (Exception exception)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                LastMatchDiagnostic = $"最近事件解析失败：{exception.GetType().Name}；原始内容未记录。",
            });
        }
    }

    private bool ValidateReadyAttestation(Process source, JsonElement root)
    {
        SidecarRuntimeAttestation? expected = Volatile.Read(ref _sidecarAttestation);
        if (expected is null
            || source.Id != expected.SidecarProcessId
            || source.HasExited
            || _identityTransactions.IsStartCancellationRequested
            || !_credentials.MatchesActiveGeneration(
                expected.AppId,
                expected.IdentityFingerprint,
                expected.IdentityGeneration))
        {
            return false;
        }

        bool echoesMatch = FeishuSidecarProtocol.MatchesReadyAttestation(
            root,
            new FeishuSidecarReadyExpectation(
                expected.RuntimeNonce,
                expected.IdentityFingerprint,
                expected.IdentityGeneration,
                expected.ParentProcessId,
                expected.ParentStartTimeUtcTicks,
                expected.ParentExecutableSha256,
                expected.ParentPayloadManifestSha256,
                FeishuSidecarProtocol.ExpectedConsumers,
                expected.SidecarExecutableSha256));
        if (!echoesMatch)
        {
            return false;
        }

        try
        {
            return source.StartTime.ToUniversalTime().Ticks == expected.SidecarStartTimeUtcTicks
                && FileAttestation.CanonicalPath(FileAttestation.ProcessImagePath(source))
                    .Equals(expected.SidecarCanonicalPath, StringComparison.OrdinalIgnoreCase)
                && FileAttestation.FixedTimeEquals(
                    FileAttestation.Sha256(expected.SidecarCanonicalPath),
                    expected.SidecarExecutableSha256);
        }
        catch
        {
            return false;
        }
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string OwnerLabel(bool bound, bool matches) =>
        !bound ? "未绑定" : matches ? "匹配" : "不匹配";

    private void ScheduleArmExpiryRefresh(
        FeishuRouteKind kind,
        FeishuRouteGateState state)
    {
        long generation = state.Generation;
        long armVersion = kind == FeishuRouteKind.Controlled
            ? state.ControlledArmVersion
            : state.ReadOnlyArmVersion;
        _armExpiryScheduler.Schedule(
            kind,
            FeishuRouteGateCoordinator.ArmedTtl,
            () =>
            {
                if (_routeGates.ExpireArmed(kind, generation, armVersion))
                {
                    UpdateSnapshot(snapshot => snapshot);
                }
            });
    }

    private void ScheduleAuthorizationExpiry(
        FeishuRouteKind kind,
        FeishuRouteGateState state)
    {
        long generation = state.Generation;
        long ticketVersion = kind == FeishuRouteKind.Controlled
            ? state.ControlledArmVersion
            : state.ReadOnlyArmVersion;
        _armExpiryScheduler.Schedule(
            kind,
            FeishuRouteGateCoordinator.AuthorizationTtl,
            () =>
            {
                if (_routeGates.ExpireAuthorization(
                    kind,
                    generation,
                    ticketVersion))
                {
                    UpdateSnapshot(snapshot => snapshot);
                }
            });
    }

    private void OnConsumerDiagnostic(object sender, DataReceivedEventArgs eventArgs)
    {
        string line = eventArgs.Data ?? string.Empty;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                Error = "飞书官方 SDK sidecar 报告连接诊断；原始诊断内容未记录。",
            });
        }
    }

    private void OnConsumerExited(object? sender, EventArgs eventArgs)
    {
        Process? completedProcess = sender as Process;
        int? exitCode = completedProcess is { HasExited: true }
            ? completedProcess.ExitCode
            : null;
        bool wasCurrent = completedProcess is not null
            && ReferenceEquals(
                Interlocked.CompareExchange(ref _consumer, null, completedProcess),
                completedProcess);
        if (!wasCurrent)
        {
            return;
        }

        Volatile.Write(ref _sidecarAttestation, null);

        FailPendingCommands();
        _armExpiryScheduler.CancelAll();

        if (completedProcess is not null)
        {
            completedProcess.OutputDataReceived -= OnConsumerOutput;
            completedProcess.ErrorDataReceived -= OnConsumerDiagnostic;
            completedProcess.Exited -= OnConsumerExited;
            completedProcess.Dispose();
        }

        if (_stopRequested)
        {
            return;
        }

        Interlocked.Exchange(ref _replyArmed, 0);
        _ownerBindingGate.Clear();
        _routeGates.Invalidate(
            _credentials.LoadOwnerBinding().IsBound,
            listening: false);

        if (exitCode == 0)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Stopped,
                Error = null,
                ReplyArmed = false,
                OwnerBindingArmed = false,
                OwnerBindingCode = null,
                OwnerBindingExpiresAt = null,
                OwnerBindingGeneration = null,
            });
            return;
        }

        UpdateSnapshot(snapshot => snapshot with
        {
            State = FeishuEventRuntimeState.Faulted,
            Error = $"飞书事件进程意外退出{(exitCode is null ? "" : $"（exit {exitCode}）")}。",
            ReplyArmed = false,
            OwnerBindingArmed = false,
            OwnerBindingCode = null,
            OwnerBindingExpiresAt = null,
            OwnerBindingGeneration = null,
        });
        _ready.TrySetException(new InvalidOperationException("飞书事件进程在 ready 前退出。"));
    }

    private async Task StopConsumerOnlyAsync()
    {
        Process? consumer = Interlocked.Exchange(ref _consumer, null);
        Volatile.Write(ref _sidecarAttestation, null);
        FailPendingCommands();
        if (consumer is null)
        {
            return;
        }

        try
        {
            if (_testHooks is not null)
            {
                if (!consumer.HasExited)
                {
                    consumer.Kill(entireProcessTree: true);
                }

                await consumer.WaitForExitAsync();
                return;
            }

            consumer.StandardInput.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await consumer.WaitForExitAsync(wait.Token);
        }
        catch
        {
            if (!consumer.HasExited)
            {
                consumer.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            consumer.OutputDataReceived -= OnConsumerOutput;
            consumer.ErrorDataReceived -= OnConsumerDiagnostic;
            consumer.Exited -= OnConsumerExited;
            consumer.Dispose();
        }
    }

    public async Task BeginMenuSetupAsync(
        bool explicitSetupOperation,
        CancellationToken cancellationToken = default)
    {
        if (!explicitSetupOperation)
        {
            throw new InvalidOperationException("写入 Bot 菜单只能由设置页上的明确操作启动。");
        }

        try
        {
            await _menuConfigurationLock.WaitAsync(cancellationToken);
            try
            {
                SidecarRuntimeAttestation attestation = RequireActiveSidecarAttestation();
                UpdateSnapshot(snapshot => snapshot with
                {
                    MenuConfigurationStage = FeishuMenuConfigurationStage.Inspecting,
                    MenuConfigurationBusy = true,
                    MenuPublishConfirmationRequired = false,
                    MenuConfigurationDetail = "正在读取远端版本并比较受管 Bot 菜单…",
                    MenuBusinessReplyError = null,
                });
                FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
                    FeishuSidecarProtocol.BuildMenuCommandJson(
                        FeishuSidecarProtocol.MenuInspectApplyCommandType,
                        NewRequestId(),
                        attestation.IdentityGeneration),
                    cancellationToken);
                ApplyMenuCommandResult(result);
            }
            finally
            {
                _menuConfigurationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            SetMenuFailure(new InvalidOperationException("菜单配置已取消；未确认任何远端变更。"));
        }
        catch (Exception exception)
        {
            SetMenuFailure(exception);
        }
    }

    public async Task RefreshMenuStatusReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        await _menuConfigurationLock.WaitAsync(cancellationToken);
        try
        {
            SidecarRuntimeAttestation attestation = RequireActiveSidecarAttestation();
            FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
                FeishuSidecarProtocol.BuildMenuCommandJson(
                    FeishuSidecarProtocol.MenuStatusCommandType,
                    NewRequestId(),
                    attestation.IdentityGeneration),
                cancellationToken);
            ApplyMenuCommandResult(result);
        }
        finally
        {
            _menuConfigurationLock.Release();
        }
    }

    private async Task InspectMenuReadOnlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _menuConfigurationLock.WaitAsync(cancellationToken);
            try
            {
                SidecarRuntimeAttestation attestation = RequireActiveSidecarAttestation();
                UpdateSnapshot(snapshot => snapshot with
                {
                    MenuConfigurationStage = FeishuMenuConfigurationStage.Inspecting,
                    MenuConfigurationBusy = true,
                    MenuPublishConfirmationRequired = false,
                    MenuConfigurationDetail = "正在只读检查远端版本和 Bot 菜单；普通启动不会写入或提交。",
                    MenuBusinessReplyError = null,
                });
                FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
                    FeishuSidecarProtocol.BuildMenuCommandJson(
                        FeishuSidecarProtocol.MenuInspectCommandType,
                        NewRequestId(),
                        attestation.IdentityGeneration),
                    cancellationToken);
                ApplyMenuCommandResult(result);
            }
            finally
            {
                _menuConfigurationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A restart/disconnect cancels inspection. It must not be promoted to a write retry.
        }
        catch (Exception exception)
        {
            SetMenuFailure(exception);
        }
    }

    private async Task PollMenuStatusAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                await _menuConfigurationLock.WaitAsync(cancellationToken);
                try
                {
                    if (Snapshot.MenuConfigurationStage is FeishuMenuConfigurationStage.VisibleVerified
                        or FeishuMenuConfigurationStage.CustomConflict
                        or FeishuMenuConfigurationStage.PublishRejected)
                    {
                        return;
                    }

                    SidecarRuntimeAttestation attestation = RequireActiveSidecarAttestation();
                    FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
                        FeishuSidecarProtocol.BuildMenuCommandJson(
                            FeishuSidecarProtocol.MenuStatusCommandType,
                            NewRequestId(),
                            attestation.IdentityGeneration),
                        cancellationToken);
                    ApplyMenuCommandResult(result);
                    if (Snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.PublishedPendingClientSync)
                    {
                        return;
                    }
                }
                finally
                {
                    _menuConfigurationLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                SetMenuFailure(exception);
                return;
            }
        }
    }

    private void ApplyMenuCommandResult(FeishuSidecarCommandResult result)
    {
        if (!result.Succeeded)
        {
            if (result.ErrorCode == 99991672)
            {
                string requiredScope = result.ErrorDiagnostic.StartsWith(
                    "required_scope:",
                    StringComparison.Ordinal)
                        ? result.ErrorDiagnostic["required_scope:".Length..]
                        : "unknown";
                throw new InvalidOperationException(
                    $"飞书菜单接口缺少 tenant scope：{requiredScope}。"
                    + "该名称来自飞书响应的预设安全白名单；未输出 token、请求正文或未知字段。"
                    + "请在开放平台当前应用的“权限管理”中开通并发布使其生效，然后回到此页重试。");
            }

            if (result.ErrorCode == 99992402)
            {
                string diagnostic = string.IsNullOrWhiteSpace(result.ErrorDiagnostic)
                    ? "field_validation_failed"
                    : result.ErrorDiagnostic;
                throw new InvalidOperationException(
                    $"飞书拒绝了菜单字段：阶段={result.ErrorStage}；{diagnostic}。诊断只包含字段路径和规则分类，不包含凭据或字段值。");
            }

            string code = result.ErrorCode?.ToString() ?? "不可用";
            throw new InvalidOperationException(
                $"飞书菜单操作失败：阶段={result.ErrorStage}；错误码={code}。未输出敏感诊断。");
        }

        FeishuMenuConfigurationStage stage = result.MenuStage switch
        {
            "written_pending_publish" => FeishuMenuConfigurationStage.WrittenPendingPublish,
            "submitted_pending_review" => FeishuMenuConfigurationStage.SubmittedPendingReview,
            "publish_rejected" => FeishuMenuConfigurationStage.PublishRejected,
            "published_pending_visibility" => FeishuMenuConfigurationStage.PublishedPendingClientSync,
            "custom_conflict" => FeishuMenuConfigurationStage.CustomConflict,
            "capability_gate" => FeishuMenuConfigurationStage.CapabilityGate,
            _ => FeishuMenuConfigurationStage.Failed,
        };
        string detail = stage switch
        {
            FeishuMenuConfigurationStage.WrittenPendingPublish =>
                "受管菜单候选已写入待发布版本；提交飞书应用版本前需要一次明确确认。",
            FeishuMenuConfigurationStage.SubmittedPendingReview =>
                $"菜单版本已提交，正在等待飞书审核或管理员处理（{result.MenuDiff}）。",
            FeishuMenuConfigurationStage.PublishRejected =>
                "菜单版本被拒绝；未自动修改权限或覆盖其他配置。",
            FeishuMenuConfigurationStage.PublishedPendingClientSync =>
                "远端版本已发布；仍需在飞书 Bot 单聊中看到菜单并完成一次真实点击回执。",
            FeishuMenuConfigurationStage.CustomConflict =>
                "检测到未知自定义菜单或链接；为避免覆盖租户配置，自动写入已停止。",
            FeishuMenuConfigurationStage.CapabilityGate =>
                "当前没有可发布的 Windows 菜单动作；本次未写入、未发布任何菜单。",
            _ => "飞书菜单返回了未识别状态；自动流程已停止。",
        };
        UpdateSnapshot(snapshot => snapshot with
        {
            MenuConfigurationStage = snapshot.MenuConfigurationStage == FeishuMenuConfigurationStage.VisibleVerified
                && stage == FeishuMenuConfigurationStage.PublishedPendingClientSync && snapshot.MenuVersion == result.Version
                    ? FeishuMenuConfigurationStage.VisibleVerified : stage,
            MenuConfigurationBusy = false,
            MenuPublishConfirmationRequired = stage is FeishuMenuConfigurationStage.WrittenPendingPublish
                or FeishuMenuConfigurationStage.PublishRejected,
            MenuConfigurationDetail = detail,
            MenuRemoteDiff = result.MenuDiff,
            MenuVersionState = result.VersionState,
            MenuVersion = result.Version,
        });
    }

    private void SetMenuFailure(Exception exception)
    {
        UpdateSnapshot(snapshot => snapshot with
        {
            MenuConfigurationStage = FeishuMenuConfigurationStage.Failed,
            MenuConfigurationBusy = false,
            MenuPublishConfirmationRequired = false,
            MenuConfigurationDetail = exception is InvalidOperationException
                ? exception.Message
                : $"菜单配置失败：{exception.GetType().Name}；未输出敏感诊断。",
        });
    }

    private SidecarRuntimeAttestation RequireActiveSidecarAttestation()
    {
        SidecarRuntimeAttestation attestation = Volatile.Read(ref _sidecarAttestation)
            ?? throw new InvalidOperationException("飞书 sidecar 身份认证已失效。");
        Process consumer = Volatile.Read(ref _consumer) is { HasExited: false } active
            ? active
            : throw new InvalidOperationException("飞书 sidecar 不在运行。");
        if (consumer.Id != attestation.SidecarProcessId
            || !_credentials.MatchesActiveGeneration(
                attestation.AppId,
                attestation.IdentityFingerprint,
                attestation.IdentityGeneration))
        {
            throw new InvalidOperationException("Bot 身份世代已变化；菜单操作被拒绝。");
        }

        return attestation;
    }

    private async Task ShowMenuCardAsync(
        string eventId,
        string senderId,
        string eventKey,
        CancellationToken sessionCancellation, bool isMenuEvent = false)
    {
        try
        {
            string? generation = _sidecarAttestation?.IdentityGeneration;
            string? version = Snapshot.MenuVersion;
            string? card = await BuildMenuCardAsync(eventKey, senderId, sessionCancellation);
            if (card is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, sessionCancellation);
            await SendP2PCardAsync(
                senderId,
                card,
                BuildIdempotencyKey(eventId, "deepori-win-card-" + eventKey),
                linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            bool ownerAuthorized = _credentials.MatchesBoundOwner(senderId);
            UpdateSnapshot(snapshot => snapshot with
            {
                MenuBusinessReplyError = null,
                MenuConfigurationStage = SetupCompletionGatePolicy.ObserveMenuReply(
                    snapshot.MenuConfigurationStage,
                    generation is not null && generation == _sidecarAttestation?.IdentityGeneration
                        && version == snapshot.MenuVersion,
                    ownerAuthorized, knownEvent: isMenuEvent, replySucceeded: true),
            });
        }
        catch (Exception exception)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                MenuBusinessReplyError = exception is InvalidOperationException ? exception.Message : $"菜单卡片发送失败：{exception.GetType().Name}",
            });
        }
    }

    private async Task ExecuteMenuCardActionAsync(
        string eventId,
        string senderId,
        string actionName,
        string token,
        CancellationToken cancellationToken)
    {
        if (!_menuCardActions.TryRemove(token, out MenuCardAction? action)
            || action.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(action.SenderId, senderId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(actionName)
                && !string.Equals(action.Action, actionName, StringComparison.Ordinal)))
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                MenuBusinessReplyError = "卡片已过期、已使用或不属于当前 owner；未执行任何操作。",
            });
            return;
        }

        try
        {
            if (action.Generation.Length > 0 && action.Generation != _sidecarAttestation?.IdentityGeneration)
                throw new InvalidOperationException("Bot 身份已变化；旧卡片已失效。");
            if (_promLight is IPromLightFleet fleet && action.Action.StartsWith("deepori.card.promlight.", StringComparison.Ordinal))
            {
                await HandleFleetCardAsync(eventId, senderId, action, fleet, cancellationToken);
                return;
            }
            if (action.AuthorizedProjectRoot is not null)
                RequireMenuProjectAuthorization(senderId, action.AuthorizedProjectRoot);
            switch (action.Action)
            {
                case "deepori.card.queue.resolve":
                {
                    FeishuQueuedInput? uncertain = TaskInputQueue.Pending().FirstOrDefault(e => e.Id == action.TaskId
                        && e.Sender == senderId && e.Phase == "submitting" && MayExecuteQueuedInput(e));
                    if (uncertain is null || _bridgeRuntime is null || _activeQueueInputs.ContainsKey(uncertain.Id))
                        throw new InvalidOperationException("提交仍在确认中或记录已变化；未解除等待。");
                    CodexThreadSummary? live = await _bridgeRuntime.GetQueuedThreadAsync(uncertain.Selection, cancellationToken);
                    if (live is null || live.Status == "active" || live.ActiveFlags.Count > 0
                        || _currentReplyReader.ReadLatestStatus(live.ThreadId) is PromLightStatus.Running or PromLightStatus.HumanGate)
                        throw new InvalidOperationException("桌面 Task 仍在运行、等待处理或状态不可确认；未解除等待。");
                    if (!MayExecuteQueuedInput(uncertain)) throw new InvalidOperationException("授权已变化；未解除等待。");
                    bool resolved = TaskInputQueue.Transition(uncertain.Id, "submitting", "finished");
                    await SendP2PMessageAsync(senderId, resolved
                        ? "已按你的确认结束本条提交记录；没有重发、没有停止 Codex。后续排队消息将按顺序继续。"
                        : "记录已变化；没有重发。", BuildIdempotencyKey(eventId, "queue-resolve"), cancellationToken);
                    return;
                }
                case "deepori.card.queue.cancel":
                {
                    FeishuQueuedInput? queued = TaskInputQueue.Pending().FirstOrDefault(e => e.Id == action.TaskId
                        && e.Sender == senderId && MayExecuteQueuedInput(e));
                    bool canceled = queued is not null && TaskInputQueue.Transition(queued.Id, "queued", "finished");
                    await SendP2PMessageAsync(senderId, canceled ? "已取消这条排队消息；当前运行中的任务不受影响。"
                        : "这条消息已开始提交或已经结束，不能再按排队消息取消。", BuildIdempotencyKey(eventId, "queue-cancel"), cancellationToken);
                    if (canceled) await UpdateQueuedCardAsync(queued!, "已取消，未执行", 0, cancellationToken);
                    return;
                }
                case "deepori.card.current.refresh":
                    await ShowMenuCardAsync(eventId, senderId, "current_task", cancellationToken);
                    return;
                case "deepori.card.selector.open":
                    await ShowMenuCardAsync(eventId, senderId, "select_task", cancellationToken);
                    return;
                case "deepori.card.selector.select":
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Windows Codex 运行时不可用；未切换 Task。");
                    }
                    FeishuProjectAuthorizationState selectionAuthorization =
                        _credentials.LoadProjectAuthorization(senderId);
                    if (!selectionAuthorization.IsAuthorized)
                    {
                        throw new InvalidOperationException("项目授权已失效；未切换 Task。");
                    }
                    string selectionProjectPath = Path.GetFullPath(
                        selectionAuthorization.ProjectPath);
                    if (!string.Equals(
                            action.AuthorizedProjectRoot,
                            selectionProjectPath,
                            StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(action.ProjectFilter))
                    {
                        throw new InvalidOperationException("项目授权已变化；未切换 Task。");
                    }
                    CodexThreadSummary? selected = await _bridgeRuntime.SelectDesktopProjectThreadAsync(
                        action.ProjectFilter,
                        action.TaskId,
                        selectionProjectPath,
                        cancellationToken);
                    if (selected is null)
                    {
                        throw new InvalidOperationException("该 Task 已不可见；未切换。");
                    }
                    // Like Mac, a successful switch immediately displays the
                    // actual current Task, rather than leaving an old empty card as feedback.
                    await ShowMenuCardAsync(eventId, senderId, "current_task", cancellationToken);
                    return;
                case "deepori.card.selector.project":
                    await ShowTaskSelectorCardAsync(
                        eventId,
                        senderId,
                        action.ProjectFilter,
                        selectionChanged: false,
                        cancellationToken);
                    return;
                case "deepori.card.selector.refresh":
                    await ShowTaskSelectorCardAsync(
                        eventId,
                        senderId,
                        projectFilter: action.ProjectFilter,
                        selectionChanged: false,
                        cancellationToken);
                    return;
                case "deepori.card.sync.project":
                case "deepori.card.sync.refresh":
                    await ShowTaskSelectorCardAsync(
                        eventId,
                        senderId,
                        projectFilter: action.ProjectFilter,
                        selectionChanged: false,
                        cancellationToken,
                        desktopSync: true);
                    return;
                case "deepori.card.sync.select":
                {
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId)
                        || string.IsNullOrWhiteSpace(action.ProjectFilter))
                    {
                        throw new InvalidOperationException("Windows Codex 运行时不可用；未接续 Task。");
                    }
                    string syncRoot = RequireMenuProjectAuthorization(senderId, action.AuthorizedProjectRoot);
                    CodexThreadSummary syncSelected = await _bridgeRuntime.SelectDesktopProjectThreadAsync(
                        action.ProjectFilter,
                        action.TaskId,
                        syncRoot,
                        cancellationToken);
                    await SendP2PCardAsync(
                        senderId,
                        BuildDesktopSyncConfirmationCard(senderId, syncSelected, syncRoot),
                        BuildIdempotencyKey(eventId, "deepori-win-sync-selected"),
                        cancellationToken);
                    return;
                }
                case "deepori.card.new.unavailable":
                    await ShowMenuCardAsync(eventId, senderId, "new_task", cancellationToken);
                    return;
                case "deepori.card.new.begin":
                {
                    ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, action.ProjectFilter, cancellationToken, includeThreads: false);
                    _pendingNewTaskProjects[senderId] = new(menu.Project.Id, menu.AuthorizedRoot);
                    await SendP2PCardAsync(
                        senderId,
                        FeishuTaskManagementCardBuilder.BuildNewTask(
                            menu.Project.Name,
                            CreateMenuCardCapability(senderId, "deepori.card.new.project", null, menu.Project.Id, menu.AuthorizedRoot),
                            CreateMenuCardCapability(senderId, "deepori.card.new.begin", null, menu.Project.Id, menu.AuthorizedRoot),
                            CreateMenuCardCapability(senderId, "deepori.card.new.cancel", null, menu.Project.Id, menu.AuthorizedRoot),
                            waitingForTitle: true, membershipNotice: "项目列表按本机已保存项目显示；新 Task 会关联到所选项目，工作目录须在原授权范围内。"),
                        BuildIdempotencyKey(eventId, "deepori-win-new-task-wait"),
                        cancellationToken);
                    return;
                }
                case "deepori.card.new.project":
                    _pendingNewTaskProjects.TryRemove(senderId, out _);
                    await SendP2PCardAsync(senderId, await BuildNewTaskMenuAsync(senderId, action.ProjectFilter, cancellationToken),
                        BuildIdempotencyKey(eventId, "deepori-win-new-task-project"), cancellationToken);
                    return;
                case "deepori.card.new.cancel":
                    _pendingNewTaskProjects.TryRemove(senderId, out _);
                    await SendP2PCardAsync(
                        senderId,
                        FeishuTaskManagementCardBuilder.BuildNewTask(
                            "",
                            CreateMenuCardCapability(senderId, "deepori.card.new.project", null, action.ProjectFilter),
                            CreateMenuCardCapability(senderId, "deepori.card.new.begin", null, action.ProjectFilter),
                            CreateMenuCardCapability(senderId, "deepori.card.selector.open", null, null),
                            canceled: true),
                        BuildIdempotencyKey(eventId, "deepori-win-new-task-cancel"),
                        cancellationToken);
                    return;
                case "deepori.card.subscription.project":
                case "deepori.card.subscription.refresh":
                    await ShowSubscriptionsCardAsync(eventId, senderId, action.ProjectFilter, null, null, cancellationToken);
                    return;
                case "deepori.card.subscription.select":
                    await ShowSubscriptionsCardAsync(eventId, senderId, action.ProjectFilter, action.TaskId, null, cancellationToken);
                    return;
                case "deepori.card.subscription.add":
                case "deepori.card.subscription.remove":
                {
                    if (string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Task 引用已失效；订阅未改变。");
                    }
                    ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, action.ProjectFilter, cancellationToken);
                    CodexProjectMenuPolicy.RequireActionableTask(menu.Threads, action.TaskId);
                    RequireMenuProjectAuthorization(senderId, menu.AuthorizedRoot);
                    bool enabled = action.Action.EndsWith(".add", StringComparison.Ordinal);
                    string subscriptionScope = SubscriptionScope(senderId);
                    if (enabled && !TaskSubscriptions.Read(subscriptionScope).ContainsKey(action.TaskId))
                        SaveReplyCursor(_sidecarAttestation!.IdentityGeneration + ":" + senderId + ":" + action.TaskId,
                            _currentReplyReader.ReadTerminalResultsAfter(action.TaskId, null).LastOrDefault()?.TurnId ?? "");
                    TaskSubscriptions.Set(subscriptionScope, action.TaskId,
                        enabled ? new CurrentTaskSelection(action.TaskId, menu.AuthorizedRoot, menu.Project.Id) : null);
                    await ShowSubscriptionsCardAsync(
                        eventId,
                        senderId,
                        action.ProjectFilter,
                        action.TaskId,
                        enabled ? "已订阅这个 Task" : "已取消订阅这个 Task",
                        cancellationToken);
                    return;
                }
                case "deepori.card.sync.confirm":
                {
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Codex Desktop Task 引用已失效；未接续。");
                    }
                    CodexThreadSummary syncCurrent = CodexProjectMenuPolicy.RequireSyncTask(
                        await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken), action.TaskId, action.ProjectFilter);
                    string syncRoot = RequireMenuProjectAuthorization(senderId, action.AuthorizedProjectRoot);
                    if (!AuthorizedProjectScope.Contains(syncRoot, syncCurrent.WorkingDirectory))
                    {
                        throw new InvalidOperationException("Task 工作目录已超出明确授权范围；未接续。");
                    }
                    CodexDesktopSyncSnapshot desktopSnapshot = _currentReplyReader.ReadDesktopSyncSnapshot(
                        syncCurrent.ThreadId);
                    string syncStatus = FeishuDesktopSyncDelivery.EffectiveStatus(syncCurrent, desktopSnapshot);
                    await SendP2PCardAsync(
                        senderId,
                        FeishuMacParityCardBuilder.BuildDesktopSync(
                            syncCurrent.Title,
                            syncCurrent.ProjectDisplayName,
                            syncStatus,
                            []),
                        BuildIdempotencyKey(eventId, "deepori-win-sync-confirmed"),
                        cancellationToken);
                    await CompleteDesktopSyncConfirmationAsync(
                        senderId,
                        syncCurrent,
                        desktopSnapshot,
                        syncStatus,
                        cancellationToken);
                    return;
                }
                case "deepori.card.sync.other":
                    await ShowTaskSelectorCardAsync(
                        eventId,
                        senderId,
                        action.ProjectFilter,
                        false,
                        cancellationToken,
                        desktopSync: true);
                    return;
                case "deepori.card.sync.cancel":
                    await SendP2PCardAsync(
                        senderId,
                        FeishuMacParityCardBuilder.BuildNotice(
                            "已取消从桌面接续",
                            "当前 Task 保持不变",
                            "本次没有接续或订阅 Codex Desktop。当前 Task 保持不变。",
                            [],
                            "grey"),
                        BuildIdempotencyKey(eventId, "deepori-win-sync-cancel"),
                        cancellationToken);
                    return;
                case "deepori.card.model.refresh":
                    await ShowMenuCardAsync(eventId, senderId, "task_settings", cancellationToken);
                    return;
                case "deepori.card.model.set":
                case "deepori.card.effort.set":
                case "deepori.card.speed.set":
                {
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("当前 Task 引用已失效；设置未修改。");
                    }
                    string value = action.ProjectFilter ?? string.Empty;
                    if (value.Length == 0)
                    {
                        throw new InvalidOperationException("设置值已失效；未修改。");
                    }
                    await _bridgeRuntime.UpdateThreadSettingsAsync(
                        action.TaskId,
                        action.Action == "deepori.card.model.set" ? value : null,
                        action.Action == "deepori.card.effort.set" ? value : null,
                        action.Action == "deepori.card.speed.set" ? value : null,
                        cancellationToken);
                    await ShowMenuCardAsync(eventId, senderId, "task_settings", cancellationToken);
                    return;
                }
                case "deepori.card.archive.confirm":
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Windows Codex 运行时不可用；未归档 Task。");
                    }
                    CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                    if (current is null || !string.Equals(current.ThreadId, action.TaskId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("当前 Task 已变化或不可见；归档确认已失效。");
                    }
                    if (string.Equals(current.Status, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("当前 Task 正在运行；不会归档。");
                    }
                    await _bridgeRuntime.ArchiveCurrentThreadAsync(cancellationToken);
                    await SendP2PCardAsync(
                        senderId,
                        FeishuMacParityCardBuilder.BuildNotice(
                            $"Task：{SafeCardText(current.Title, 80)}",
                            $"项目：{SafeCardText(ProjectDisplayName(current.WorkingDirectory), 80)}",
                            $"已归档：**{SafeCardText(current.Title, 80)} · {SafeCardText(ProjectDisplayName(current.WorkingDirectory), 80)}**\n\n可在下方立即撤销归档。",
                            [CreateCardButton(senderId, "deepori.card.archive.restore", "撤销归档", "primary_filled", current.ThreadId)],
                            "grey"),
                        BuildIdempotencyKey(eventId, "deepori-win-archive-task"),
                        cancellationToken);
                    return;
                case "deepori.card.archive.restore":
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("已归档 Task 引用已失效；未恢复。");
                    }
                    await _bridgeRuntime.RestoreThreadAsync(action.TaskId, cancellationToken);
                    await ShowMenuCardAsync(eventId, senderId, "current_task", cancellationToken);
                    return;
                case "deepori.card.archive.cancel":
                    await SendP2PMessageAsync(
                        senderId,
                        "已取消归档；当前 Task 未变化。",
                        BuildIdempotencyKey(eventId, "deepori-win-archive-cancel"),
                        cancellationToken);
                    return;
                case "deepori.card.model.cycle":
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Windows Codex 运行时不可用；未修改模型。");
                    }
                    CodexThreadSummary? modelTarget = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                    if (modelTarget is null
                        || !string.Equals(modelTarget.ThreadId, action.TaskId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("当前 Task 已变化或不可见；模型设置已失效。");
                    }
                    await _bridgeRuntime.CycleCurrentThreadModelAsync(cancellationToken);
                    await ShowMenuCardAsync(eventId, senderId, "task_settings", cancellationToken);
                    return;
                case "deepori.card.compact.confirm":
                    if (_bridgeRuntime is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("Windows Codex 运行时不可用；未压缩上下文。");
                    }
                    CodexThreadSummary? compactTarget = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                    if (compactTarget is null
                        || !string.Equals(compactTarget.ThreadId, action.TaskId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("当前 Task 已变化或不可见；压缩确认已失效。");
                    }
                    string compactResult = await _bridgeRuntime.CompactCurrentThreadAsync(cancellationToken);
                    await SendP2PMessageAsync(
                        senderId,
                        compactResult,
                        BuildIdempotencyKey(eventId, "deepori-win-compact-task"),
                        cancellationToken);
                    return;
                case "deepori.card.compact.cancel":
                    await SendP2PMessageAsync(
                        senderId,
                        "已取消上下文压缩；当前 Task 未变化。",
                        BuildIdempotencyKey(eventId, "deepori-win-compact-cancel"),
                        cancellationToken);
                    return;
                case "deepori.card.usage.refresh":
                    if (_bridgeRuntime is null
                        || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                    {
                        throw new InvalidOperationException("Windows Codex 当前未连接；无法刷新额度。");
                    }
                    await _bridgeRuntime.RefreshUsageAsync(force: true, cancellationToken);
                    await ShowMenuCardAsync(eventId, senderId, "codex_usage", cancellationToken);
                    return;
                case "deepori.card.promlight.refresh":
                    if (_promLight is not null)
                    {
                        await _promLight.RefreshAsync(cancellationToken);
                    }
                    await ShowMenuCardAsync(eventId, senderId, "promlight", cancellationToken);
                    return;
                case "deepori.card.promlight.back":
                    await ShowMenuCardAsync(eventId, senderId, "promlight", cancellationToken);
                    return;
                case "deepori.card.promlight.legend":
                    await ShowMenuCardAsync(eventId, senderId, "promlight_legend", cancellationToken);
                    return;
                case "deepori.card.promlight.tasks.open":
                    await ShowPromLightTaskManagerAsync(
                        eventId,
                        senderId,
                        projectFilter: null,
                        selectedTaskId: null,
                        change: null,
                        cancellationToken);
                    return;
                case "deepori.card.promlight.tasks.project":
                    await ShowPromLightTaskManagerAsync(
                        eventId,
                        senderId,
                        action.ProjectFilter,
                        selectedTaskId: null,
                        change: null,
                        cancellationToken);
                    return;
                case "deepori.card.promlight.tasks.select":
                    await ShowPromLightTaskManagerAsync(
                        eventId,
                        senderId,
                        action.ProjectFilter,
                        action.TaskId,
                        change: null,
                        cancellationToken);
                    return;
                case "deepori.card.promlight.tasks.add":
                case "deepori.card.promlight.tasks.remove":
                {
                    if (_promLight is null || string.IsNullOrWhiteSpace(action.TaskId))
                    {
                        throw new InvalidOperationException("PromLight 或 Task 引用不可用；未改变关联。" );
                    }
                    ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, action.ProjectFilter, cancellationToken);
                    CodexThreadSummary target = CodexProjectMenuPolicy.RequireActionableTask(menu.Threads, action.TaskId);
                    RequireMenuProjectAuthorization(senderId, menu.AuthorizedRoot);
                    bool enabled = action.Action.EndsWith(".add", StringComparison.Ordinal);
                    _promLight.SetTaskAssociation(target.ThreadId, enabled);
                    await _promLight.RefreshAsync(cancellationToken);
                    await ShowPromLightTaskManagerAsync(
                        eventId,
                        senderId,
                        menu.Project.Id,
                        target.ThreadId,
                        enabled ? "已关联这个 Task" : "已取消关联这个 Task",
                        cancellationToken);
                    return;
                }
                case "deepori.card.promlight.settings.open":
                    await ShowPromLightSettingsCardAsync(
                        eventId,
                        senderId,
                        cancellationToken);
                    return;
                case "deepori.card.promlight.settings.clear":
                    if (_promLight is null)
                    {
                        throw new InvalidOperationException("PromLight Windows 服务不可用。" );
                    }
                    await _promLight.ClearTaskAssociationsAsync(cancellationToken);
                    await ShowMenuCardAsync(eventId, senderId, "promlight", cancellationToken);
                    return;
                default:
                    throw new InvalidOperationException("未识别的卡片操作；未执行任何操作。");
            }
        }
        catch (Exception exception)
        {
            string safeError = exception is InvalidOperationException
                ? exception.Message
                : $"菜单卡片操作失败：{exception.GetType().Name}；未输出敏感诊断。";
            UpdateSnapshot(snapshot => snapshot with
            {
                MenuBusinessReplyError = safeError,
            });
            try
            {
                await SendP2PCardAsync(
                    senderId,
                    BuildMenuCard("操作未完成", safeError, []),
                    BuildIdempotencyKey(eventId, "deepori-win-card-error"),
                    cancellationToken);
            }
            catch
            {
                // The original safe error remains visible in local diagnostics.
            }
        }
    }

    private async Task<string?> BuildMenuCardAsync(
        string eventKey,
        string senderId,
        CancellationToken cancellationToken)
    {
        switch (eventKey)
        {
            case "current_task":
            {
                if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                {
                    return BuildMenuCard(
                        "当前 Task",
                        "Windows Codex 当前未连接。连接恢复前不会推断或切换 Task。",
                        [CreateCardButton(senderId, "deepori.card.selector.open", "查看可见 Task", "primary")]);
                }
                CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                if (current is null)
                {
                    // An empty current route needs an actionable selector, not
                    // another notice requiring a second click to reach the same flow.
                    return await BuildTaskSelectorMenuCardAsync(
                        senderId,
                        projectFilter: null,
                        selectionChanged: false,
                        cancellationToken,
                        choosingCurrent: true);
                }
                string project = !string.IsNullOrWhiteSpace(current.ProjectDisplayName)
                    ? SafeCardText(current.ProjectDisplayName, 80)
                    : string.IsNullOrWhiteSpace(current.WorkingDirectory)
                    ? "未提供"
                    : SafeCardText(Path.GetFileName(current.WorkingDirectory), 80);
                FeishuEventRuntimeSnapshot eventSnapshot = Snapshot;
                FeishuQueuedInput[] taskInputs = TaskInputQueue.Pending()
                    .Where(e => e.Selection.ThreadId == current.ThreadId && MayExecuteQueuedInput(e)).ToArray();
                int queuedCount = taskInputs.Count(e => e.Phase == "queued");
                bool routeExecuting = eventSnapshot.ReadOnlyRouteExecuting
                    || taskInputs.Any(e => e.Phase == "running")
                    || _currentReplyReader.ReadLatestStatus(current.ThreadId) is PromLightStatus.Running or PromLightStatus.HumanGate;
                bool routePending = queuedCount > 0 || eventSnapshot.ReadOnlyRouteCapturePending;
                string status = FeishuCurrentTaskCardBuilder.ProjectStatus(
                    current.Status,
                    routeExecuting,
                    routePending);
                return FeishuCurrentTaskCardBuilder.Build(
                    current.Title,
                    project,
                    status,
                    routeExecuting || FeishuCurrentTaskCardBuilder.IsRunning(current.Status) ? 1 : 0,
                    queuedCount + (eventSnapshot.ReadOnlyRouteCapturePending ? 1 : 0),
                    DateTimeOffset.Now,
                    CreateMenuCardCapability(senderId, "deepori.card.current.refresh", current.ThreadId, projectFilter: null),
                    CreateMenuCardCapability(senderId, "deepori.card.selector.open", taskId: null, projectFilter: null),
                    new CodexRolloutTaskStateReader().ReadLatestReply(current.ThreadId), CodexProjectMenuPolicy.MembershipNotice([current]));
            }
            case "select_task":
            {
                return await BuildTaskSelectorMenuCardAsync(
                    senderId,
                    projectFilter: null,
                    selectionChanged: false,
                    cancellationToken);
            }
            case "new_task":
            {
                return await BuildNewTaskMenuAsync(senderId, null, cancellationToken);
            }
            case "archive_task":
            {
                if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                {
                    return FeishuMacParityCardBuilder.BuildNotice(
                        "归档 Codex Task",
                        "没有当前 Task",
                        "Windows Codex 当前未连接；未归档任何 Task。",
                        [],
                        "yellow");
                }
                CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                if (current is null)
                {
                    return FeishuMacParityCardBuilder.BuildNotice(
                        "归档 Codex Task",
                        "没有当前 Task",
                        "尚未选择 Task。请先点击机器人菜单中的“切换 Task”。",
                        [],
                        "yellow");
                }
                bool archiveBusy = string.Equals(
                    current.Status,
                    "running",
                    StringComparison.OrdinalIgnoreCase);
                string archiveProject = ProjectDisplayName(current.WorkingDirectory);
                IReadOnlyList<object> archiveActions = archiveBusy
                    ? []
                    : [
                        CreateCardButton(
                            senderId,
                            "deepori.card.archive.confirm",
                            "归档这个 Task…",
                            "danger",
                            current.ThreadId,
                            confirm: true,
                            confirmTitle: "确认归档这个 Task？",
                            confirmText: $"{SafeCardText(current.Title, 80)} · {SafeCardText(archiveProject, 80)}"),
                        CreateCardButton(senderId, "deepori.card.archive.cancel", "取消，不归档", "default"),
                    ];
                return FeishuMacParityCardBuilder.BuildArchiveTask(
                    current.Title,
                    archiveProject,
                    archiveBusy,
                    archiveActions);
            }
            case "task_subscriptions":
            {
                return await BuildSubscriptionsCardAsync(senderId, null, null, null, cancellationToken);
            }
            case "sync_desktop":
            {
                if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                {
                    return FeishuMacParityCardBuilder.BuildNotice(
                        "从 Codex Desktop 回到飞书",
                        "Codex Desktop 未连接",
                        "Windows Codex 当前未连接，尚未接续任何 Task。",
                        []);
                }
                CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                if (current is null)
                {
                    string selectorRoot = RequireMenuProjectAuthorization(senderId);
                    return FeishuMacParityCardBuilder.BuildNotice(
                        "从 Codex Desktop 回到飞书",
                        "没有有效的当前 Task",
                        "当前没有可接续的 Codex Desktop Task。",
                        [CreateCardButton(senderId, "deepori.card.sync.other", "接续其他 Task", "default",
                            projectFilter: null, authorizedProjectRoot: selectorRoot)]);
                }
                string syncRoot = RequireMenuProjectAuthorization(senderId);
                return BuildDesktopSyncConfirmationCard(senderId, current, syncRoot);
            }
            case "sync_desktop_switch":
                return await BuildTaskSelectorMenuCardAsync(
                    senderId,
                    projectFilter: null,
                    selectionChanged: false,
                    cancellationToken,
                    desktopSync: true);
            case "task_settings":
            {
                if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                {
                    return FeishuMacParityCardBuilder.BuildNotice(
                        "修改当前 Task 模型",
                        "Codex Desktop 未连接",
                        "Windows Codex 当前未连接；未修改模型。",
                        []);
                }
                CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                if (current is null)
                {
                    return BuildMenuCard("修改当前 Task 模型", "当前没有可见的 Codex Task。", []);
                }
                IReadOnlyList<CodexModelSummary> catalog = await _bridgeRuntime.GetModelsAsync(cancellationToken);
                CodexModelSummary? selectedModel = catalog.FirstOrDefault(model =>
                    string.Equals(model.ModelId, current.Model, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.FirstOrDefault();
                FeishuModelOption[] models = catalog.Select(model => new FeishuModelOption(
                    model.DisplayName,
                    CreateMenuCardCapability(senderId, "deepori.card.model.set", current.ThreadId, model.ModelId),
                    string.Equals(model.ModelId, current.Model, StringComparison.OrdinalIgnoreCase))).ToArray();
                FeishuModelOption[] efforts = (selectedModel?.AvailableReasoningEfforts ?? Array.Empty<string>())
                    .Select(effort => new FeishuModelOption(
                        ReasoningEffortLabel(effort),
                        CreateMenuCardCapability(senderId, "deepori.card.effort.set", current.ThreadId, effort),
                        string.Equals(effort, current.ReasoningEffort, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var tierValues = new List<(string Id, string Label)> { ("default", "标准") };
                if (selectedModel is not null)
                {
                    tierValues.AddRange(selectedModel.AvailableServiceTiers.Select(tier =>
                        (tier.Id, string.Equals(tier.Name, "fast", StringComparison.OrdinalIgnoreCase)
                            ? "快速 · 约 1.5 倍速度，额度消耗更高"
                            : string.IsNullOrWhiteSpace(tier.Name) ? tier.Id : tier.Name)));
                }
                FeishuModelOption[] tiers = tierValues
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(tier => new FeishuModelOption(
                        tier.Label,
                        CreateMenuCardCapability(senderId, "deepori.card.speed.set", current.ThreadId, tier.Id),
                        string.Equals(tier.Id, current.ServiceTier, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                return FeishuTaskManagementCardBuilder.BuildModelSettings(
                    current,
                    ProjectDisplayName(current.WorkingDirectory),
                    models,
                    efforts,
                    tiers,
                    CreateMenuCardCapability(senderId, "deepori.card.model.refresh", current.ThreadId, null));
            }
            case "compact_task_context":
            {
                if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
                {
                    return BuildMenuCard("压缩当前 Task 上下文", "Windows Codex 当前未连接；未执行压缩。", []);
                }
                CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(cancellationToken);
                if (current is null)
                {
                    return BuildMenuCard("压缩当前 Task 上下文", "当前没有可见的 Codex Task。", []);
                }
                string compactProject = ProjectDisplayName(current.WorkingDirectory);
                return FeishuMacParityCardBuilder.BuildCompactTaskContext(
                    current.Title,
                    compactProject,
                    [CreateCardButton(
                        senderId,
                        "deepori.card.compact.confirm",
                        "确认压缩当前 Task 上下文…",
                        "default",
                        current.ThreadId,
                        confirm: true,
                        confirmTitle: "确认压缩当前 Task 上下文？",
                        confirmText: "Codex 会总结较早内容。只有确认后才会执行，取消不会改变当前 Task。")]);
            }
            case "codex_usage":
            {
                CodexUsageSnapshot usage = _bridgeRuntime?.UsageSnapshot ?? CodexUsageSnapshot.Initial;
                IReadOnlyList<object> usageActions = _bridgeRuntime is { Snapshot.State: BridgeRuntimeState.Running }
                    ? [CreateCardButton(senderId, "deepori.card.usage.refresh", "刷新用量", "primary")]
                    : [];
                return FeishuMacParityCardBuilder.BuildUsage(usage, usageActions);
            }
            case "promlight":
            {
                if (_promLight is IPromLightFleet fleet)
                    return await BuildFleetCardAsync(senderId, fleet, cancellationToken);
                if (_promLight is null)
                {
                    return BuildMenuCard("我的提示灯", "PromLight Windows 服务不可用。", []);
                }
                PromLightDeviceSnapshot device = await _promLight.DiscoverAsync(cancellationToken);
                bool supported = device.Found
                    && device.Opened
                    && device.ReleaseNumber == PromLightProtocol.SupportedReleaseNumber;
                if (!supported)
                {
                    string error = string.IsNullOrWhiteSpace(device.Error)
                        ? "设备未通过 Windows HID 协议 Gate。"
                        : SafeCardText(device.Error, 300);
                    return BuildMenuCard(
                        "我的提示灯",
                        error,
                        [CreateCardButton(senderId, "deepori.card.promlight.refresh", "重新检测", "primary")]);
                }
                return FeishuPromLightCardBuilder.BuildControl(
                    device,
                    _promLight.Activity,
                    _promLight.Binding,
                    await PromLightAssociatedTaskLabelsAsync(senderId, cancellationToken),
                    CreateMenuCardCapability(senderId, "deepori.card.promlight.tasks.open", taskId: null, projectFilter: null),
                    CreateMenuCardCapability(senderId, "deepori.card.promlight.settings.open", taskId: null, projectFilter: null),
                    CreateMenuCardCapability(senderId, "deepori.card.promlight.refresh", taskId: null, projectFilter: null),
                    CreateMenuCardCapability(senderId, "deepori.card.promlight.legend", taskId: null, projectFilter: null));
            }
            case "promlight_legend":
                return FeishuPromLightCardBuilder.BuildLegend(
                    CreateMenuCardCapability(senderId, "deepori.card.promlight.back", taskId: null, projectFilter: null));
            default:
                return null;
        }
    }

    private async Task ShowPromLightTaskManagerAsync(
        string eventId,
        string senderId,
        string? projectFilter,
        string? selectedTaskId,
        string? change,
        CancellationToken cancellationToken)
    {
        string card = await BuildPromLightTaskManagerAsync(
            senderId,
            projectFilter,
            selectedTaskId,
            change,
            cancellationToken);
        await SendP2PCardAsync(
            senderId,
            card,
            BuildIdempotencyKey(eventId, "deepori-win-promlight-tasks"),
            cancellationToken);
    }

    private async Task CompleteNewTaskAsync(
        string eventId,
        string senderId,
        PendingNewTaskProject pending,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
            {
                throw new InvalidOperationException("Windows Codex 当前未连接；未创建 Task。");
            }
            RequireMenuProjectAuthorization(senderId, pending.AuthorizedRoot);
            string normalizedTitle = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalizedTitle.Length is < 1 or > 120)
            {
                throw new InvalidOperationException("Task 标题必须为 1 到 120 个字符；请重新从“新建 Task”开始。");
            }
            await _bridgeRuntime.CreateDesktopProjectThreadAsync(pending.ProjectId, pending.AuthorizedRoot, normalizedTitle, cancellationToken,
                () => { RequireMenuProjectAuthorization(senderId, pending.AuthorizedRoot); return true; });
            await ShowMenuCardAsync(eventId, senderId, "current_task", cancellationToken);
        }
        catch (Exception exception)
        {
            string safeError = exception is InvalidOperationException
                ? exception.Message
                : $"新建 Task 失败：{exception.GetType().Name}。";
            try
            {
                await SendP2PCardAsync(
                    senderId,
                    BuildMenuCard("新建 Codex Task 未完成", safeError, []),
                    BuildIdempotencyKey(eventId, "deepori-win-new-task-error"),
                    cancellationToken);
            }
            catch
            {
            }
        }
    }

    private async Task ShowSubscriptionsCardAsync(
        string eventId,
        string senderId,
        string? projectFilter,
        string? selectedTaskId,
        string? change,
        CancellationToken cancellationToken)
    {
        string card = await BuildSubscriptionsCardAsync(
            senderId,
            projectFilter,
            selectedTaskId,
            change,
            cancellationToken);
        await SendP2PCardAsync(
            senderId,
            card,
            BuildIdempotencyKey(eventId, "deepori-win-task-subscriptions"),
            cancellationToken);
    }

    private async Task<string> BuildSubscriptionsCardAsync(
        string senderId,
        string? projectFilter,
        string? selectedTaskId,
        string? change,
        CancellationToken cancellationToken)
    {
        ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, projectFilter, cancellationToken);
        string project = menu.Project.Name;
        IReadOnlyList<CodexThreadSummary> threads = menu.Threads;
        string Token(string action, string? taskId = null) => CreateMenuCardCapability(
            senderId, action, taskId, menu.Project.Id, menu.AuthorizedRoot);
        Dictionary<string, CurrentTaskSelection> subscriptions = TaskSubscriptions.Read(SubscriptionScope(senderId));
        CodexThreadSummary[] selectable = threads.Where(thread => thread.AuthorizedForRouting).ToArray();
        CodexThreadSummary? selected = selectable.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, selectedTaskId, StringComparison.Ordinal))
            ?? selectable.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, _bridgeRuntime?.SelectedThreadId, StringComparison.Ordinal))
            ?? selectable.FirstOrDefault();
        var options = new List<FeishuManagedTaskOption>(threads.Count);
        foreach (CodexThreadSummary thread in threads)
        {
            options.Add(new FeishuManagedTaskOption(
                thread.Title,
                project,
                thread.AuthorizedForRouting ? Token("deepori.card.subscription.select", thread.ThreadId) : "",
                selected is not null && string.Equals(thread.ThreadId, selected.ThreadId, StringComparison.Ordinal),
                subscriptions.ContainsKey(thread.ThreadId), thread.AuthorizedForRouting, thread.WorkingDirectory));
        }
        IReadOnlyList<string> labels = threads
            .Where(thread => thread.AuthorizedForRouting && subscriptions.ContainsKey(thread.ThreadId))
            .Select(thread => $"{thread.Title} · {project}")
            .ToArray();
        return FeishuTaskManagementCardBuilder.BuildSubscriptions(
            project,
            Token("deepori.card.subscription.project"),
            options,
            labels,
            selected is null ? "" : Token("deepori.card.subscription.add", selected.ThreadId),
            selected is null ? "" : Token("deepori.card.subscription.remove", selected.ThreadId),
            Token("deepori.card.subscription.refresh"),
            change, MenuProjectOptions(senderId, menu, "deepori.card.subscription.project"), CodexProjectMenuPolicy.MembershipNotice(threads));
    }

    private sealed record ProjectMenuSnapshot(string AuthorizedRoot, IReadOnlyList<CodexDesktopProject> Projects,
        CodexDesktopProject Project, IReadOnlyList<CodexThreadSummary> Threads);

    private string RequireMenuProjectAuthorization(string senderId, string? expectedRoot = null)
    {
        FeishuProjectAuthorizationState authorization = _credentials.LoadProjectAuthorization(senderId);
        if (!authorization.IsAuthorized)
            throw new InvalidOperationException("没有明确授权的项目目录；不会列出 Task。");
        string root = Path.GetFullPath(authorization.ProjectPath);
        if (expectedRoot is not null && !string.Equals(Path.TrimEndingDirectorySeparator(root),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("项目授权已变化，请重新打开菜单。");
        return root;
    }

    private async Task<ProjectMenuSnapshot> GetProjectMenuAsync(string senderId, string? projectFilter,
        CancellationToken cancellationToken, bool includeThreads = true)
    {
        if (_bridgeRuntime is null || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
            throw new InvalidOperationException("Windows Codex 当前未连接；无法读取 Task。");
        string root = RequireMenuProjectAuthorization(senderId);
        IReadOnlyList<CodexDesktopProject> projects = await _bridgeRuntime.GetDesktopProjectsAsync(root, cancellationToken);
        CodexDesktopProject project = CodexProjectMenuPolicy.SelectProject(projects, projectFilter, _bridgeRuntime.SelectedDesktopProjectId);
        IReadOnlyList<CodexThreadSummary> threads = includeThreads
            ? await _bridgeRuntime.GetDesktopProjectThreadsAsync(project.Id, root, cancellationToken) : [];
        RequireMenuProjectAuthorization(senderId, root);
        return new(root, projects, project, threads.Where(thread => !thread.Archived).ToArray());
    }

    private FeishuProjectCardOption[] MenuProjectOptions(string senderId, ProjectMenuSnapshot menu, string action)
        => menu.Projects.Select(project => new FeishuProjectCardOption(project.Name,
            CreateMenuCardCapability(senderId, action, null, project.Id, menu.AuthorizedRoot), project.Id == menu.Project.Id)).ToArray();

    private async Task<string> BuildNewTaskMenuAsync(string senderId, string? projectFilter, CancellationToken cancellationToken)
    {
        ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, projectFilter, cancellationToken, includeThreads: false);
        return FeishuTaskManagementCardBuilder.BuildNewTask(menu.Project.Name,
            CreateMenuCardCapability(senderId, "deepori.card.new.project", null, menu.Project.Id, menu.AuthorizedRoot),
            CreateMenuCardCapability(senderId, "deepori.card.new.begin", null, menu.Project.Id, menu.AuthorizedRoot),
            CreateMenuCardCapability(senderId, "deepori.card.new.cancel", null, menu.Project.Id, menu.AuthorizedRoot),
            projects: MenuProjectOptions(senderId, menu, "deepori.card.new.project"),
            membershipNotice: "项目列表按本机已保存项目显示；新 Task 会关联到所选项目，工作目录须在原授权范围内。");
    }

    private static string ReasoningEffortLabel(string effort) => effort.ToLowerInvariant() switch
    {
        "minimal" => "极低 · minimal",
        "low" => "低 · low",
        "medium" => "中 · medium",
        "high" => "高 · high",
        "xhigh" => "超高 · xhigh",
        "max" => "最高 · max",
        "ultra" => "极致 · ultra",
        _ => effort,
    };

    private async Task<string> BuildPromLightTaskManagerAsync(
        string senderId,
        string? projectFilter,
        string? selectedTaskId,
        string? change,
        CancellationToken cancellationToken)
    {
        if (_promLight is null)
        {
            return BuildMenuCard("关联 Task", "PromLight Windows 服务不可用。", []);
        }

        ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, projectFilter, cancellationToken);
        string project = menu.Project.Name;
        IReadOnlyList<CodexThreadSummary> threads = menu.Threads;

        PromLightBindingSnapshot binding = _promLight.Binding;
        CodexThreadSummary[] selectable = threads.Where(thread => thread.AuthorizedForRouting).ToArray();
        CodexThreadSummary? selected = selectable.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, selectedTaskId, StringComparison.Ordinal))
            ?? selectable.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, _bridgeRuntime?.SelectedThreadId, StringComparison.Ordinal))
            ?? selectable.FirstOrDefault();
        string projectToken = CreateMenuCardCapability(
            senderId,
            "deepori.card.promlight.tasks.project",
            taskId: null,
            projectFilter: menu.Project.Id, authorizedProjectRoot: menu.AuthorizedRoot);
        var options = new List<FeishuPromLightTaskOption>(threads.Count);
        foreach (CodexThreadSummary thread in threads)
        {
            options.Add(new FeishuPromLightTaskOption(
                thread.Title,
                project,
                thread.AuthorizedForRouting ? CreateMenuCardCapability(
                    senderId,
                    "deepori.card.promlight.tasks.select",
                    thread.ThreadId,
                    menu.Project.Id, menu.AuthorizedRoot) : "",
                selected is not null
                    && string.Equals(thread.ThreadId, selected.ThreadId, StringComparison.Ordinal),
                binding.TaskIds.Contains(thread.ThreadId, StringComparer.Ordinal), thread.AuthorizedForRouting, thread.WorkingDirectory));
        }

        string? selectedId = selected?.ThreadId;
        return FeishuPromLightCardBuilder.BuildTaskManager(
            binding.Name,
            project,
            projectToken,
            options,
            AssociatedTaskLabels(binding, threads),
            CreateMenuCardCapability(
                senderId,
                "deepori.card.promlight.tasks.add",
                selectedId,
                menu.Project.Id, menu.AuthorizedRoot),
            CreateMenuCardCapability(
                senderId,
                "deepori.card.promlight.tasks.remove",
                selectedId,
                menu.Project.Id, menu.AuthorizedRoot),
            CreateMenuCardCapability(senderId, "deepori.card.promlight.back", taskId: null, projectFilter: null),
            CreateMenuCardCapability(senderId, "deepori.card.promlight.legend", taskId: null, projectFilter: null),
            change, MenuProjectOptions(senderId, menu, "deepori.card.promlight.tasks.project"), CodexProjectMenuPolicy.MembershipNotice(threads));
    }

    private async Task ShowPromLightSettingsCardAsync(
        string eventId,
        string senderId,
        CancellationToken cancellationToken)
    {
        if (_promLight is null)
        {
            throw new InvalidOperationException("PromLight Windows 服务不可用。" );
        }
        string card = FeishuPromLightCardBuilder.BuildDeviceSettings(
            _promLight.Binding,
            CreateMenuCardCapability(senderId, "deepori.card.promlight.settings.clear", taskId: null, projectFilter: null),
            CreateMenuCardCapability(senderId, "deepori.card.promlight.back", taskId: null, projectFilter: null));
        await SendP2PCardAsync(
            senderId,
            card,
            BuildIdempotencyKey(eventId, "deepori-win-promlight-settings"),
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> PromLightAssociatedTaskLabelsAsync(
        string senderId,
        CancellationToken cancellationToken)
    {
        if (_promLight is null)
        {
            return [];
        }
        try
        {
            ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, null, cancellationToken, includeThreads: false);
            var threads = new List<CodexThreadSummary>();
            foreach (CodexDesktopProject project in menu.Projects)
                threads.AddRange(await _bridgeRuntime!.GetDesktopProjectThreadsAsync(project.Id, menu.AuthorizedRoot, cancellationToken));
            RequireMenuProjectAuthorization(senderId, menu.AuthorizedRoot);
            return AssociatedTaskLabels(_promLight.Binding, threads);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> AssociatedTaskLabels(
        PromLightBindingSnapshot binding,
        IReadOnlyList<CodexThreadSummary> threads)
    {
        Dictionary<string, CodexThreadSummary> visible = threads.Where(thread => thread.AuthorizedForRouting && !thread.Archived)
            .DistinctBy(thread => thread.ThreadId).ToDictionary(
            thread => thread.ThreadId,
            StringComparer.Ordinal);
        return binding.TaskIds
            .Where(visible.ContainsKey)
            .Select(taskId =>
            {
                CodexThreadSummary thread = visible[taskId];
                return $"{thread.Title} · {thread.ProjectDisplayName}";
            })
            .ToArray();
    }

    private async Task ShowTaskSelectorCardAsync(
        string eventId,
        string senderId,
        string? projectFilter,
        bool selectionChanged,
        CancellationToken cancellationToken,
        bool desktopSync = false)
    {
        string card = await BuildTaskSelectorMenuCardAsync(
            senderId,
            projectFilter,
            selectionChanged,
            cancellationToken,
            desktopSync: desktopSync);
        await SendP2PCardAsync(
            senderId,
            card,
            BuildIdempotencyKey(eventId, "deepori-win-task-selector"),
            cancellationToken);
    }

    private string BuildDesktopSyncConfirmationCard(
        string senderId,
        CodexThreadSummary task,
        string authorizedRoot)
    {
        if (string.IsNullOrWhiteSpace(task.EffectiveProjectId)
            || !AuthorizedProjectScope.Contains(authorizedRoot, task.WorkingDirectory))
        {
            throw new InvalidOperationException("Task 的项目归属或目录授权无法核实；未生成接续操作。");
        }
        CodexDesktopSyncSnapshot snapshot = _currentReplyReader.ReadDesktopSyncSnapshot(task.ThreadId);
        string status = FeishuDesktopSyncDelivery.EffectiveStatus(task, snapshot);
        string project = string.IsNullOrWhiteSpace(task.ProjectDisplayName)
            ? ProjectDisplayName(task.WorkingDirectory)
            : task.ProjectDisplayName;
        return FeishuTaskManagementCardBuilder.BuildDesktopSyncConfirmation(
            task,
            project,
            status,
            CreateMenuCardCapability(senderId, "deepori.card.sync.confirm", task.ThreadId,
                task.EffectiveProjectId, authorizedRoot),
            CreateMenuCardCapability(senderId, "deepori.card.sync.other", null,
                _bridgeRuntime?.SelectedDesktopProjectId, authorizedRoot),
            CreateMenuCardCapability(senderId, "deepori.card.sync.cancel", task.ThreadId,
                task.EffectiveProjectId, authorizedRoot));
    }

    private async Task<string> BuildTaskSelectorMenuCardAsync(
        string senderId,
        string? projectFilter,
        bool selectionChanged,
        CancellationToken cancellationToken,
        bool choosingCurrent = false,
        bool desktopSync = false)
    {
        if (_bridgeRuntime is null
            || _bridgeRuntime.Snapshot.State != BridgeRuntimeState.Running)
        {
            return BuildMenuCard(
                "切换 Codex Task",
                "Windows Codex 当前未连接；没有可选择的 Task。",
                []);
        }

        FeishuProjectAuthorizationState authorization =
            _credentials.LoadProjectAuthorization(senderId);
        if (!authorization.IsAuthorized)
        {
            return BuildMenuCard(
                "切换 Codex Task",
                "没有明确授权的项目目录；不会列出或切换 Task。",
                []);
        }

        ProjectMenuSnapshot menu = await GetProjectMenuAsync(senderId, projectFilter, cancellationToken);
        string projectPath = menu.AuthorizedRoot;
        string projectId = menu.Project.Id;
        string project = menu.Project.Name;
        IReadOnlyList<CodexThreadSummary> threads = menu.Threads;
        string selectedId = _bridgeRuntime.SelectedThreadId;
        CodexThreadSummary? selected = threads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, selectedId, StringComparison.Ordinal));

        FeishuProjectCardOption[] projectOptions = MenuProjectOptions(
            senderId,
            menu,
            desktopSync ? "deepori.card.sync.project" : "deepori.card.selector.project");
        var options = new List<FeishuTaskCardOption>(threads.Count);
        foreach (CodexThreadSummary thread in threads)
        {
            string token = thread.AuthorizedForRouting ? CreateMenuCardCapability(
                senderId,
                desktopSync ? "deepori.card.sync.select" : "deepori.card.selector.select",
                thread.ThreadId,
                projectFilter: projectId,
                authorizedProjectRoot: projectPath) : string.Empty;
            options.Add(new FeishuTaskCardOption(
                thread.Title,
                project,
                token,
                selected is not null
                    && string.Equals(
                        thread.ThreadId,
                        selected.ThreadId,
                        StringComparison.Ordinal),
                thread.AuthorizedForRouting,
                thread.WorkingDirectory));
        }
        string refreshToken = CreateMenuCardCapability(
            senderId,
            desktopSync ? "deepori.card.sync.refresh" : "deepori.card.selector.refresh",
            taskId: null,
            projectFilter: projectId,
            authorizedProjectRoot: projectPath);
        return FeishuTaskSelectorCardBuilder.Build(
            projectOptions,
            options,
            refreshToken,
            selectionChanged,
            choosingCurrent, CodexProjectMenuPolicy.MembershipNotice(threads));
    }

    private static bool IsWithinAuthorizedProject(
        string authorizedProjectPath,
        string? candidateWorkingDirectory) =>
        AuthorizedProjectScope.Contains(authorizedProjectPath, candidateWorkingDirectory);

    private object CreateCardButton(
        string senderId,
        string action,
        string label,
        string buttonType,
        string? taskId = null,
        string? projectFilter = null,
        string? authorizedProjectRoot = null,
        bool confirm = false,
        string? confirmTitle = null,
        string? confirmText = null)
    {
        string token = CreateMenuCardCapability(
            senderId, action, taskId, projectFilter, authorizedProjectRoot);

        var result = new Dictionary<string, object?>
        {
            ["tag"] = "button",
            ["text"] = new { tag = "plain_text", content = SafeCardText(label, 40) },
            ["type"] = buttonType,
            ["name"] = action,
            ["value"] = new { deepori_token = token },
        };
        if (confirm)
        {
            result["confirm"] = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = SafeCardText(confirmTitle ?? "确认执行？", 100),
                },
                text = new
                {
                    tag = "plain_text",
                    content = SafeCardText(confirmText ?? "只有确认后才会执行。", 200),
                },
            };
        }
        return result;
    }

    private string CreateMenuCardCapability(
        string senderId,
        string action,
        string? taskId,
        string? projectFilter,
        string? authorizedProjectRoot = null,
        PromLightLamp? lamp = null)
    {
        foreach ((string existingToken, MenuCardAction existing) in _menuCardActions)
        {
            if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _menuCardActions.TryRemove(existingToken, out _);
            }
        }
        string token;
        do
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }
        while (!_menuCardActions.TryAdd(token, new MenuCardAction(
            senderId,
            action,
            taskId,
            projectFilter,
            DateTimeOffset.UtcNow.AddMinutes(5),
            authorizedProjectRoot, lamp?.Id, lamp?.Revision, _sidecarAttestation?.IdentityGeneration ?? "")));
        return token;
    }

    private static string BuildMenuCard(string title, string body, IEnumerable<object> buttons) =>
        FeishuMacParityCardBuilder.BuildNotice(
            SafeCardText(title, 100),
            "DeepOri Bridge for Windows",
            SafeCardText(body, 1000),
            buttons.ToArray());

    private static string ProjectDisplayName(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return "未提供";
        }

        try
        {
            string fullPath = Path.GetFullPath(workingDirectory);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
            return string.IsNullOrWhiteSpace(name) ? fullPath : name;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return "未提供";
        }
    }

    private static string SafeCardText(string? value, int maxLength)
    {
        string normalized = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private async Task SendP2PMessageAsync(
        string recipientId,
        string replyText,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        string requestId = NewRequestId();
        FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
            FeishuSidecarProtocol.BuildSendP2PCommandJson(
                requestId,
                recipientId,
                replyText,
                idempotencyKey),
            cancellationToken);
        if (!result.Succeeded)
        {
            string code = result.ErrorCode?.ToString() ?? "不可用";
            throw new InvalidOperationException(
                $"飞书菜单回复失败：阶段={result.ErrorStage}；错误码={code}。未输出身份或正文。" );
        }
    }

    private async Task SendP2PCardAsync(string recipientId, string cardJson, string idempotencyKey, CancellationToken cancellationToken)
    {
        FeishuSidecarCommandResult result = await SendSidecarCommandAsync(
            FeishuSidecarProtocol.BuildSendCardCommandJson(NewRequestId(), recipientId, cardJson, idempotencyKey),
            cancellationToken);
        if (!result.Succeeded)
        {
            string code = result.ErrorCode?.ToString() ?? "不可用";
            throw new InvalidOperationException($"飞书菜单卡片发送失败：阶段={result.ErrorStage}；错误码={code}。 ");
        }
    }

    private async Task<FeishuSidecarCommandResult> SendSidecarCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        Process consumer = Volatile.Read(ref _consumer) is { HasExited: false } running
            ? running
            : throw new InvalidOperationException("飞书官方 SDK sidecar 不在运行；命令未发送。");
        var completion = new TaskCompletionSource<FeishuSidecarCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using JsonDocument document = JsonDocument.Parse(command);
        string requestId = document.RootElement.GetProperty("request_id").GetString()
            ?? throw new InvalidOperationException("sidecar 命令缺少请求引用。");
        if (!_pendingCommands.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法登记飞书 SDK 命令。");
        }

        try
        {
            await _consumerInputLock.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref _consumer), consumer)
                    || consumer.HasExited)
                {
                    throw new InvalidOperationException("飞书 sidecar 已更换或退出；命令未发送。");
                }
                await consumer.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
                await consumer.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _consumerInputLock.Release();
            }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            string type = document.RootElement.GetProperty("type").GetString() ?? "";
            if (type is "menu_inspect_apply" or "menu_publish" or "menu_inspect" or "menu_status")
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    string generation = document.RootElement.TryGetProperty("expected_identity_generation", out var value) ? value.GetString() ?? "" : "";
                    await SendSidecarCommandAsync(JsonSerializer.Serialize(new { type = "cancel_command", request_id = NewRequestId(),
                        target_request_id = requestId, expected_identity_generation = generation }), deadline.Token);
                }
                catch (Exception) { /* Remote completion may already have happened; never claim rollback. */ }
            }
            throw;
        }
        finally
        {
            _pendingCommands.TryRemove(requestId, out _);
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private async Task ReplyToAutomaticMessageAsync(
        string messageId,
        string replyText,
        string idempotencyPurpose,
        CancellationToken sessionCancellation)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                sessionCancellation);
            await ReplyToMessageAsync(
                messageId,
                replyText,
                idempotencyPurpose,
                linked.Token);

            UpdateSnapshot(snapshot => snapshot with
            {
                ReplySentCount = snapshot.ReplySentCount + 1,
                LastReplyAt = DateTimeOffset.Now,
                ReplyError = null,
            });
        }
        catch (Exception exception)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                ReplyError = exception is InvalidOperationException
                    ? exception.Message
                    : $"Bot 回执失败：{exception.GetType().Name}",
            });
        }
    }

    private void RetireInvalidQueuedInputs()
    {
        FeishuStoredCredential identity = _credentials.Load();
        if (!FirstConnectionNavigationGate.IsCurrentlyVerified(identity)
            || _credentials.LoadOwnerBinding().Error is not null) return;
        TaskInputQueue.RetireQueued(input =>
        {
            if (input.Generation != identity.IdentityGeneration) return true;
            if (!_credentials.MatchesBoundOwner(input.Sender)) return true;
            FeishuProjectAuthorizationState scope = _credentials.LoadProjectAuthorization(input.Sender);
            if (scope.Error is not null) return false;
            return !scope.IsAuthorized || !AuthorizedProjectScope.Contains(scope.ProjectPath, input.Task.WorkingDirectory)
                || !AuthorizedProjectScope.Contains(scope.ProjectPath, input.Selection.ProjectRoot)
                || _credentials.CurrentTaskSelectionScope(input.Sender) != input.AuthorizationScope;
        });
    }

    private bool MayExecuteQueuedInput(FeishuQueuedInput input) =>
        Snapshot.State == FeishuEventRuntimeState.Listening
        && _sidecarAttestation?.IdentityGeneration == input.Generation
        && _credentials.CurrentTaskSelectionScope(input.Sender) == input.AuthorizationScope
        && _credentials.MatchesBoundOwner(input.Sender)
        && _credentials.LoadProjectAuthorization(input.Sender) is { IsAuthorized: true } scope
        && IsWithinAuthorizedProject(Path.GetFullPath(scope.ProjectPath), input.Selection.ProjectRoot)
        && IsWithinAuthorizedProject(Path.GetFullPath(scope.ProjectPath), input.Task.WorkingDirectory);

    private async Task AcceptTaskInputAsync(string messageId, string senderId, string prompt,
        string authorizedProjectPath, CancellationToken token, JsonElement? resourceEvent = null)
    {
        // Capture before the first await: a later menu selection cannot retarget it.
        CurrentTaskSelection selection = _bridgeRuntime?.CaptureSelection() ?? CurrentTaskSelection.Empty;
        string authorizationScope = _credentials.CurrentTaskSelectionScope(senderId) ?? "";
        string generation = _sidecarAttestation?.IdentityGeneration ?? "";
        bool locked = false;
        try
        {
            await _inputAcceptanceLock.WaitAsync(token);
            locked = true;
            CodexThreadSummary? task = selection == CurrentTaskSelection.Empty ? null
                : await _bridgeRuntime!.GetQueuedThreadAsync(selection, token);
            if (task is null) throw new InvalidOperationException("尚未选择可用的 Task，请先切换 Task；本条消息未执行。");
            if (!IsWithinAuthorizedProject(Path.GetFullPath(authorizedProjectPath), task.WorkingDirectory))
                throw new InvalidOperationException("当前 Task 不在授权项目内；未接收指令。");
            var input = new FeishuQueuedInput(FeishuTaskInputQueue.Key(generation, senderId, messageId),
                messageId, senderId, generation, selection, task with { Preview = "" }, prompt, resourceEvent?.GetRawText(), DateTimeOffset.UtcNow,
                AuthorizationScope: authorizationScope);
            if (!MayExecuteQueuedInput(input)) throw new InvalidOperationException("飞书身份或授权已变化；未接收指令。");
            RetireInvalidQueuedInputs();
            (bool added, int position) = TaskInputQueue.Enqueue(input);
            if (!added)
            {
                FeishuAttachments.Trace(messageId, "duplicate-suppressed");
                return;
            }
            FeishuAttachments.Trace(messageId, "queued", position);
            await UpdateQueuedCardAsync(input, "已收到，已排队；上一轮结束后按顺序执行", position, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Persisted queue survives reconnect. */ }
        catch (Exception exception)
        {
            string safe = exception is InvalidOperationException ? exception.Message
                : "消息队列暂不可用，尚未确认接收；请检查桥接状态。";
            UpdateSnapshot(s => s with { ReadOnlyRouteError = safe });
            try { await ReplyToMessageAsync(messageId, safe, "queue-accept-error", token); } catch { }
        }
        finally { if (locked) _inputAcceptanceLock.Release(); }
    }

    private async Task UpdateQueuedCardAsync(FeishuQueuedInput input, string status, int position, CancellationToken token)
    {
        if (!MayExecuteQueuedInput(input)) return;
        // Refresh the five-minute cancellation capability while a long Task runs.
        string cardSession = _sidecarAttestation!.RuntimeNonce + ":" + input.Id;
        string stateKey = cardSession + ":" + status + ":" + position + ":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 120;
        if (_queueCardStates.TryGetValue(input.Id, out string? shown) && shown == stateKey) return;
        string card = FeishuTaskFeedback.Card(input.Task.Title,
            string.IsNullOrEmpty(input.Task.ProjectDisplayName) ? Path.GetFileName(input.Task.WorkingDirectory) : input.Task.ProjectDisplayName,
            status + (position > 0 ? $"\n队列位置：{position}（含等待执行的前序消息）" : ""),
            position > 0 ? "blue" : "orange", DateTimeOffset.UtcNow - input.CreatedAt);
        if (position > 0 || input.Phase == "submitting")
        {
            bool uncertain = input.Phase == "submitting";
            string actionName = uncertain ? "deepori.card.queue.resolve" : "deepori.card.queue.cancel";
            string capability = CreateMenuCardCapability(input.Sender, actionName, input.Id,
                null, input.Selection.ProjectRoot);
            var document = System.Text.Json.Nodes.JsonNode.Parse(card)!;
            document["body"]!["elements"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
            {
                tag = "button", text = new { tag = "plain_text", content = uncertain ? "已在桌面确认本条处理完毕，解除等待" : "取消这条排队消息" },
                type = "default", name = actionName, value = new { deepori_token = capability },
                confirm = new { title = new { tag = "plain_text", content = "确认处理这条记录？" },
                    text = new { tag = "plain_text", content = uncertain
                        ? "请先在桌面确认本条已处理完毕。此操作仅关闭不确定的提交记录，不重发也不停止任务；后续队列会继续。"
                        : "仅取消尚未提交的排队消息，不影响运行中的 Task。" } },
            }));
            card = document.ToJsonString();
        }
        try
        {
            string cardId = _queueOwnedCards.GetValueOrDefault(cardSession) ?? "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            FeishuSidecarCommandResult sent = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
            {
                type = cardId.Length == 0 ? "task_card_create" : "task_card_patch", request_id = NewRequestId(),
                recipient_id = input.Sender, message_id = cardId, card,
                idempotency_key = BuildIdempotencyKey(input.MessageId, "task-progress"),
            }), deadline.Token);
            if (sent.Succeeded)
            {
                if (cardId.Length == 0 && sent.MessageId.Length > 0)
                {
                    // Reissuing the same idempotent create reacquires ownership in
                    // a new sidecar session; never bypass its owned-card patch gate.
                    TaskInputQueue.SetCard(input.Id, sent.MessageId);
                    _queueOwnedCards[cardSession] = sent.MessageId;
                    FeishuSidecarCommandResult refreshed = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
                    {
                        type = "task_card_patch", request_id = NewRequestId(), message_id = sent.MessageId, card,
                    }), deadline.Token);
                    if (!refreshed.Succeeded) return; // Refresh a restored card's old cancellation capability.
                }
                _queueCardStates[input.Id] = stateKey;
            }
        }
        catch (Exception) { /* Queue is already durable. Worker retries the same card key. */ }
    }

    private async Task PumpTaskInputsAsync(CancellationToken token)
    {
        if (_lastInputSweep < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            _lastInputSweep = DateTimeOffset.UtcNow;
            try
            {
                var pendingIds = TaskInputQueue.Pending().Select(input => input.Id).ToHashSet(StringComparer.Ordinal);
                FeishuAttachmentSession.Sweep(_currentReplyReader, pendingIds.Contains);
            }
            catch (Exception) { /* Queue/ownership read failure postpones cleanup. */ }
        }
        if (_bridgeRuntime?.Snapshot.State != BridgeRuntimeState.Running) return;
        RetireInvalidQueuedInputs();
        FeishuQueuedInput[] pending = TaskInputQueue.Pending().Where(MayExecuteQueuedInput).ToArray();
        foreach (FeishuQueuedInput head in FeishuTaskInputQueue.Heads(pending))
        {
            IEnumerable<FeishuQueuedInput> group = pending.Where(e => e.Selection.ThreadId == head.Selection.ThreadId);
            int position = 0;
            foreach (FeishuQueuedInput queued in group.Where(e => e.Phase == "queued"))
                if (!_activeQueueInputs.ContainsKey(queued.Id))
                    await UpdateQueuedCardAsync(queued, "已收到，已排队；上一轮结束后按顺序执行", ++position, token);
            if (!MayExecuteQueuedInput(head) || _activeQueueInputs.ContainsKey(head.Id)) continue;
            if (head.Phase == "running")
            {
                CodexControlledRouteResult? result = _currentReplyReader.ReadTerminalResult(head.Selection.ThreadId, head.TurnId);
                if (result is not null)
                {
                    if (await SaveRecoveredQueueResultAsync(head, result, token))
                        TaskInputQueue.Transition(head.Id, "running", "finished");
                }
                continue;
            }
            if (head.Phase == "submitting")
            {
                await UpdateQueuedCardAsync(head, "提交确认中断，未重发；请在桌面端确认本条消息状态，后续消息暂缓", 0, token);
                continue; // Never guess whether an uncertain request executed.
            }
            if (head.Phase != "queued" || head.AvailableAt > DateTimeOffset.UtcNow || _activeQueueInputs.Count >= 2) continue;
            CodexThreadSummary? live = await _bridgeRuntime.GetQueuedThreadAsync(head.Selection, token);
            if (live is null || !string.Equals(live.WorkingDirectory, head.Task.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                await UpdateQueuedCardAsync(head, "原 Task 已不可用或目录已变化；未执行", 0, token);
                TaskInputQueue.Transition(head.Id, "queued", "finished");
                continue;
            }
            if (live.Status == "active" || live.ActiveFlags.Any(f => f is "waitingOnApproval" or "waitingOnUserInput")
                || _currentReplyReader.ReadLatestStatus(live.ThreadId) is PromLightStatus.Running or PromLightStatus.HumanGate) continue;
            if (!_activeQueueInputs.TryAdd(head.Id, 0)) continue;
            _ = RunQueuedInputAsync(head, token: _routeGates.SessionCancellation);
        }
    }

    private async Task RunQueuedInputAsync(FeishuQueuedInput input, CancellationToken token)
    {
        try
        {
            using JsonDocument? resources = input.ResourceJson is null ? null : JsonDocument.Parse(input.ResourceJson);
            await ExecuteCurrentTaskPromptAsync(input.MessageId, input.Sender, input.Prompt,
                input.Selection.ProjectRoot, token, resources?.RootElement, input);
        }
        catch (CodexTaskBusyException)
        {
            // Only an explicit non-acceptance may release the durable submit claim.
            TaskInputQueue.RequeueBusy(input.Id);
            _queueCardStates.TryRemove(input.Id, out _);
        }
        catch (FeishuQueueInputCanceledException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            UpdateSnapshot(s => s with { ReadOnlyRouteError = "队列处理遇到异常；记录已保留，未自动重发已提交的指令。" });
        }
        finally { _activeQueueInputs.TryRemove(input.Id, out _); }
    }

    private async Task<bool> SaveRecoveredQueueResultAsync(FeishuQueuedInput input, CodexControlledRouteResult result, CancellationToken token)
    {
        if (!MayExecuteQueuedInput(input)) return false;
        string id = $"turn:{input.Generation}:{input.Sender}:{result.ThreadId}:{result.TurnId}";
        string label = result.Status == "completed" ? "已完成" : result.Status == "interrupted" ? "已停止" : "未完成";
        string text = string.IsNullOrWhiteSpace(result.AgentMessageText) ? $"本轮{label}，没有文字结果，请在 Codex Desktop 查看。" : result.AgentMessageText;
        FeishuResultPart[] parts = FeishuTaskFeedback.SplitUtf8(text).Select((chunk, index) => new FeishuResultPart(
            $"Task：{SafeCardText(input.Task.Title, 100)} · {label}\n\n{chunk}", BuildIdempotencyKey(id, $"task-result-{index}"))).ToArray();
        FeishuResultPart[] resources = await ResultResources.CaptureAsync(id, text + "\n"
            + _currentReplyReader.ReadGeneratedImageLinks(result.ThreadId, result.TurnId), input.Task.WorkingDirectory,
            index => BuildIdempotencyKey(id, $"task-resource-{index}"), token);
        await TaskResultOutbox.SaveAsync(id, new(input.Generation, input.Sender, input.Task.WorkingDirectory,
            parts.Concat(resources).ToArray()), token);
        await UpdateQueuedCardAsync(input, $"{label}，结果已保留并等待回传", 0, token);
        return true;
    }

    private async Task ExecuteCurrentTaskPromptAsync(
        string messageId,
        string senderId,
        string prompt,
        string authorizedProjectPath,
        CancellationToken sessionCancellation,
        JsonElement? resourceEvent = null, FeishuQueuedInput? queuedInput = null)
    {
        BridgeRuntime runtime = _bridgeRuntime
            ?? throw new InvalidOperationException("Codex 运行时不可用。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, sessionCancellation);
        try
        {
            CodexThreadSummary? current = queuedInput?.Task ?? await runtime.GetCurrentThreadAsync(linked.Token);
            if (current is null)
            {
                throw new InvalidOperationException("尚未选择当前 Task。请先使用“切换 Task”。");
            }
            if (!IsWithinAuthorizedProject(Path.GetFullPath(authorizedProjectPath), current.WorkingDirectory))
            {
                throw new InvalidOperationException("当前 Task 不在授权项目中；未发送消息。");
            }
            await TrySetPromLightStatusAsync(PromLightStatus.Running);
            string generation = _sidecarAttestation?.IdentityGeneration
                ?? throw new InvalidOperationException("飞书身份世代不可用，未执行指令。");
            string deliveryId = messageId;
            string resultResourceText = "";
            bool terminalKnown = false;
            int resultTextPartCount = 0;
            FeishuResultPart[] storedParts = [];
            var feedback = new FeishuTaskFeedback(
                async (card, token) =>
                {
                    EnsureRecipient();
                    string cardSession = _sidecarAttestation!.RuntimeNonce + ":" + queuedInput?.Id;
                    if (queuedInput is not null && _queueOwnedCards.TryGetValue(cardSession, out string? ownedCard)) return ownedCard;
                    FeishuSidecarCommandResult sent = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
                    {
                        type = "task_card_create", request_id = NewRequestId(), recipient_id = senderId,
                        card, idempotency_key = BuildIdempotencyKey(messageId, "task-progress"),
                    }), token);
                    if (!sent.Succeeded || sent.MessageId.Length == 0)
                        throw new InvalidOperationException("任务状态卡发送未确认。");
                    if (queuedInput is not null)
                    {
                        TaskInputQueue.SetCard(queuedInput.Id, sent.MessageId);
                        _queueOwnedCards[cardSession] = sent.MessageId;
                    }
                    return sent.MessageId;
                },
                async (cardId, card, token) =>
                {
                    EnsureRecipient();
                    FeishuSidecarCommandResult updated = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
                    {
                        type = "task_card_patch", request_id = NewRequestId(), message_id = cardId, card,
                    }), token);
                    if (!updated.Succeeded) throw new InvalidOperationException("任务状态卡更新未确认。");
                },
                async (text, purpose, token) =>
                {
                    EnsureRecipient();
                    string key = BuildIdempotencyKey(deliveryId, "task-" + purpose);
                    if (!purpose.StartsWith("result-", StringComparison.Ordinal))
                        await SendP2PMessageAsync(senderId, text, key, token);
                    else
                    {
                        await TaskResultOutbox.SendPartAsync(deliveryId, key, MayDeliverTaskResult, SendStoredTaskResultAsync, token);
                        // Only the final text chunk owns resource delivery. The card
                        // cannot claim delivery completed before those sends confirm.
                        int index = Array.FindIndex(storedParts, p => p.Key == key);
                        if (index == resultTextPartCount - 1)
                            foreach (FeishuResultPart part in storedParts.Skip(resultTextPartCount))
                                await TaskResultOutbox.SendPartAsync(deliveryId, part.Key, MayDeliverTaskResult, SendStoredTaskResultAsync, token);
                    }
                },
                prepareResults: async (parts, token) =>
                {
                    resultTextPartCount = parts.Length;
                    FeishuResultPart[] resources = await ResultResources.CaptureAsync(deliveryId,
                        resultResourceText, current.WorkingDirectory,
                        index => BuildIdempotencyKey(deliveryId, $"task-resource-{index}"), token);
                    storedParts = parts.Select(part => part with { Key = BuildIdempotencyKey(deliveryId, "task-" + part.Key) })
                        .Concat(resources).ToArray();
                    await TaskResultOutbox.SaveAsync(deliveryId,
                        new FeishuResultEnvelope(generation, senderId, current.WorkingDirectory, storedParts), token);
                    if (queuedInput is not null)
                    {
                        TaskInputQueue.Transition(queuedInput.Id, "queued", "finished");
                        if (terminalKnown) TaskInputQueue.Transition(queuedInput.Id, "running", "finished");
                    }
                }, durableQueue: queuedInput is not null);
            FeishuTaskFeedbackOutcome outcome = await feedback.RunAsync(
                current.Title,
                string.IsNullOrEmpty(current.ProjectDisplayName) ? Path.GetFileName(current.WorkingDirectory) : current.ProjectDisplayName,
                async (started, token) =>
                {
                    using var input = new FeishuAttachmentSession(current.ThreadId, queuedInput?.Id ?? "");
                    try
                    {
                        if (resourceEvent is JsonElement resources)
                        {
                            EnsureRecipient();
                            FeishuAttachments.Trace(messageId, "received", resourceEvent: resources);
                            if (resources.TryGetProperty("resource_error", out JsonElement error)
                                && error.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(error.GetString()))
                            {
                                string diagnostic = FeishuAttachments.ReadDiagnostic(resources);
                                string reason = "附件消息无法处理：" + error.GetString()![..Math.Min(300, error.GetString()!.Length)]
                                    + (diagnostic.Length == 0 ? "" : $"（诊断：{diagnostic}）");
                                UpdateSnapshot(snapshot => snapshot with { LastMatchDiagnostic = reason });
                                throw new InvalidOperationException(reason);
                            }
                            IReadOnlyList<FeishuInputResource> references = FeishuAttachments.ReadResources(resources);
                            bool plainPost = resources.TryGetProperty("message_type", out JsonElement inputType)
                                && inputType.GetString() == "post"
                                && resources.TryGetProperty("content", out JsonElement postText)
                                && postText.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(postText.GetString());
                            if (references.Count == 0 && !plainPost)
                                throw new InvalidOperationException("没有可读取的附件；当前支持图片、文档和音频，不支持视频、压缩包或可执行文件。");
                            if (references.Count > 0)
                                await SendP2PMessageAsync(senderId, $"已收到 {references.Count} 个附件，正在安全下载；下载完整后才会交给当前 Task。",
                                    BuildIdempotencyKey(messageId, "attachment-received"), token);
                            foreach (FeishuInputResource reference in references)
                            {
                                for (int attempt = 0; attempt < 2; attempt++)
                                {
                                    EnsureRecipient();
                                    string destination = input.NewDownloadPath();
                                    using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                                    downloadTimeout.CancelAfter(TimeSpan.FromSeconds(125));
                                    FeishuSidecarCommandResult download = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
                                    {
                                        type = "resource_download", request_id = NewRequestId(), message_id = messageId,
                                        file_key = reference.Key, resource_kind = reference.Kind, output_path = destination,
                                    }), downloadTimeout.Token);
                                    if (download.Succeeded)
                                    {
                                        input.Attachments.Add(FeishuAttachments.ValidateDownloaded(destination, reference));
                                        break;
                                    }
                                    if (attempt == 0 && download.ErrorStage is "busy" or "download_transport" or "download_io" or "timeout")
                                    {
                                        await Task.Delay(750, token);
                                        continue;
                                    }
                                    throw new InvalidOperationException($"附件下载失败（{download.ErrorStage} / {download.ErrorCode?.ToString() ?? "无代码"}）；请检查机器人消息资源读取权限或重新发送。本条任务未执行。");
                                }
                            }
                            FeishuAttachments.Trace(messageId, "downloaded", input.Attachments.Count);
                            EnsureRecipient();
                        }
                    }
                    catch (InvalidOperationException exception)
                    {
                        // Only input preparation is covered: a transport error after
                        // starting a turn must never be reported as "not executed".
                        if (resourceEvent is not null) FeishuAttachments.Trace(messageId, "rejected", resourceEvent: resourceEvent);
                        throw new FeishuInputRejectedException(exception.Message);
                    }
                    catch (OperationCanceledException) when (!sessionCancellation.IsCancellationRequested)
                    {
                        if (resourceEvent is not null) FeishuAttachments.Trace(messageId, "rejected");
                        throw new FeishuInputRejectedException("附件准备超时，尚未发送给 Codex；本条消息未执行。");
                    }
                    // Retain input on an uncertain start/timeout: Desktop may still be
                    // reading it. Confirmed terminal results allow deterministic cleanup.
                    input.MayBeInUse = input.Attachments.Count > 0;
                    CodexControlledRouteResult result;
                    try
                    {
                    result = await runtime.RunPromptInCurrentThreadAsync(prompt, token,
                        started, current.ThreadId, input.Attachments, () =>
                        {
                            try { EnsureRecipient(); return true; }
                            catch (InvalidOperationException) { return false; }
                            catch (OperationCanceledException) { return false; }
                        }, queuedSelection: queuedInput?.Selection, onSubmitting: queuedInput is null ? null : () =>
                        {
                            EnsureRecipient();
                            if (!TaskInputQueue.Transition(queuedInput.Id, "queued", "submitting"))
                                throw new FeishuQueueInputCanceledException();
                            FeishuAttachments.Trace(messageId, "submitting");
                        }, onNotAccepted: queuedInput is null ? null : () =>
                        {
                            input.MayBeInUse = false;
                            if (!TaskInputQueue.Transition(queuedInput.Id, "submitting", "queued"))
                                throw new InvalidOperationException("未能释放未送达消息的提交记录；未重发。");
                        }, onTurnStarted: turnId =>
                        {
                            input.RecordTurn(turnId);
                            _bridgeTurnIds.TryAdd(turnId, senderId);
                            if (queuedInput is not null && !TaskInputQueue.Transition(queuedInput.Id, "submitting", "running", turnId))
                                throw new InvalidOperationException("任务已提交，但运行记录更新失败；未重发指令。");
                            if (_promLight is IPromLightFleet fleet) fleet.RecordTurnOrigin(senderId, current.ThreadId, turnId);
                            if (resourceEvent is not null) FeishuAttachments.Trace(messageId, "submitted", input.Attachments.Count);
                        });
                    }
                    catch (CodexTaskBusyException) { input.MayBeInUse = false; throw; }
                    catch (Exception) when (queuedInput is not null
                        && TaskInputQueue.Pending().Any(e => e.Id == queuedInput.Id && e.Phase is "submitting" or "running"))
                    {
                        FeishuQueuedInput saved = TaskInputQueue.Pending().Single(e => e.Id == queuedInput.Id);
                        result = new(current.ThreadId, saved.TurnId, "pending", "");
                    }
                    terminalKnown = CodexAppServerClient.IsTerminalTurnStatus(result.Status);
                    input.MayBeInUse = result.Status == "pending" && input.Attachments.Count > 0;
                    if (resourceEvent is not null && result.Status is "completed" or "pending")
                        FeishuAttachments.Trace(messageId, result.Status);
                    if (result.Status == "pending")
                    {
                        // The wait notice must not occupy the final result's durable
                        // deduplication key. Release ownership to the completion observer.
                        _bridgeTurnIds.TryRemove(result.TurnId, out _);
                        _observedCurrentReplies.TryUpdate(generation + ":" + senderId + ":" + current.ThreadId, "", result.TurnId);
                    }
                    else
                    {
                        deliveryId = $"turn:{generation}:{senderId}:{result.ThreadId}:{result.TurnId}";
                        resultResourceText = result.AgentMessageText + "\n"
                            + _currentReplyReader.ReadGeneratedImageLinks(result.ThreadId, result.TurnId);
                    }
                    return result;
                },
                linked.Token, sessionCancellation);
            await TrySetPromLightStatusAsync(outcome.TaskSucceeded || outcome.Pending ? PromLightStatus.Idle : PromLightStatus.Error);
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteReplySentCount = snapshot.ReadOnlyRouteReplySentCount + (outcome.Delivered ? 1 : 0),
                LastReadOnlyRouteReplyAt = outcome.Delivered ? DateTimeOffset.Now : snapshot.LastReadOnlyRouteReplyAt,
                ReadOnlyRouteError = outcome.Error,
            });

            void EnsureRecipient()
            {
                sessionCancellation.ThrowIfCancellationRequested();
                if (queuedInput is not null && !MayExecuteQueuedInput(queuedInput))
                    throw new InvalidOperationException("排队消息的身份或项目授权已失效；未发送消息。");
                if (Snapshot.State != FeishuEventRuntimeState.Listening
                    || _sidecarAttestation?.IdentityGeneration != generation
                    || !_credentials.MatchesBoundOwner(senderId)
                    || _credentials.LoadProjectAuthorization(senderId) is not { IsAuthorized: true } liveScope
                    || !IsWithinAuthorizedProject(Path.GetFullPath(liveScope.ProjectPath), current.WorkingDirectory))
                    throw new InvalidOperationException("飞书会话或项目授权已失效，停止投递。");
            }
        }
        catch (CodexTaskBusyException) when (queuedInput is not null) { throw; }
        catch (FeishuQueueInputCanceledException) when (queuedInput is not null) { throw; }
        catch (OperationCanceledException) when (queuedInput is not null && sessionCancellation.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await TrySetPromLightStatusAsync(PromLightStatus.Error);
            string safeError = exception is InvalidOperationException
                ? exception.Message
                : $"当前 Task 执行失败：{exception.GetType().Name}。";
            UpdateSnapshot(snapshot => snapshot with { ReadOnlyRouteError = safeError });
            try
            {
                using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
                deliveryTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await ReplyToMessageAsync(messageId, safeError,
                    "deepori-win-current-task-error", deliveryTimeout.Token);
            }
            catch
            {
            }
        }
    }

    private bool MayDeliverTaskResult(FeishuResultEnvelope result) =>
        Snapshot.State == FeishuEventRuntimeState.Listening
        && _sidecarAttestation?.IdentityGeneration == result.Generation
        && _credentials.MatchesBoundOwner(result.Sender)
        && _credentials.LoadProjectAuthorization(result.Sender) is { IsAuthorized: true } scope
        && IsWithinAuthorizedProject(Path.GetFullPath(scope.ProjectPath), result.WorkingDirectory);

    private async Task CompleteDesktopSyncConfirmationAsync(
        string sender,
        CodexThreadSummary task,
        CodexDesktopSyncSnapshot snapshot,
        string effectiveStatus,
        CancellationToken token)
    {
        string generation = _sidecarAttestation?.IdentityGeneration
            ?? throw new InvalidOperationException("飞书身份世代已失效；未接续。");
        DesktopSyncSubscriptions.Clear(generation, sender);
        CodexControlledRouteResult? latestCompleted = _currentReplyReader.ReadLatestCompletedResult(task.ThreadId);
        string scope = generation + ":" + sender + ":" + task.ThreadId;
        if (effectiveStatus == "running")
        {
            DesktopSyncSubscriptions.Save(new FeishuDesktopSyncSubscription(
                generation,
                sender,
                task.ThreadId,
                Path.GetFullPath(task.WorkingDirectory),
                task.Title,
                string.IsNullOrWhiteSpace(task.ProjectDisplayName)
                    ? ProjectDisplayName(task.WorkingDirectory) : task.ProjectDisplayName,
                task.EffectiveProjectId,
                snapshot.Status == "running" ? snapshot.TurnId : "",
                latestCompleted?.TurnId ?? "",
                DateTimeOffset.UtcNow));
            SaveReplyCursor(scope, latestCompleted?.TurnId ?? "");
            return;
        }

        CodexControlledRouteResult? terminal = snapshot.TerminalResult(task.ThreadId);
        if (terminal is null)
        {
            SaveReplyCursor(scope, latestCompleted?.TurnId ?? "");
            return;
        }
        await QueueDesktopSyncResultAsync(
            generation,
            sender,
            task.Title,
            task.WorkingDirectory,
            terminal,
            token);
        SaveReplyCursor(scope, terminal.TurnId);
        await FlushTaskResultOutboxWithoutFailingConfirmationAsync(token);
    }

    private async Task QueueDesktopSyncResultAsync(
        string generation,
        string sender,
        string title,
        string workingDirectory,
        CodexControlledRouteResult result,
        CancellationToken token)
    {
        string id = $"turn:{generation}:{sender}:{result.ThreadId}:{result.TurnId}";
        FeishuResultPart[] textParts = FeishuDesktopSyncDelivery.TextParts(
            title,
            result,
            index => BuildIdempotencyKey(id, $"task-result-{index}"));
        var envelope = new FeishuResultEnvelope(
            generation,
            sender,
            Path.GetFullPath(workingDirectory),
            textParts);
        if (!MayDeliverTaskResult(envelope))
        {
            throw new InvalidOperationException("桌面接续结果的身份或项目授权已变化；停止投递。");
        }
        if (result.Status == "completed")
        {
            FeishuResultPart[] resources = await ResultResources.CaptureAsync(
                id,
                result.AgentMessageText + "\n"
                    + _currentReplyReader.ReadGeneratedImageLinks(result.ThreadId, result.TurnId),
                envelope.WorkingDirectory,
                index => BuildIdempotencyKey(id, $"task-resource-{index}"),
                token);
            envelope = envelope with { Parts = envelope.Parts.Concat(resources).ToArray() };
        }
        await TaskResultOutbox.SaveAsync(id, envelope, token);
    }

    private async Task FlushTaskResultOutboxWithoutFailingConfirmationAsync(CancellationToken token)
    {
        try
        {
            await TaskResultOutbox.FlushAsync(
                MayDeliverTaskResult,
                SendStoredTaskResultAsync,
                token);
            UpdateSnapshot(snapshot => snapshot with { ReadOnlyRouteError = null });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteError = exception is InvalidOperationException
                    ? exception.Message
                    : "桌面结果已安全保存，飞书投递尚未完成；稍后自动重试。",
            });
        }
    }

    private async Task ObserveDesktopSyncSubscriptionsAsync(CancellationToken token)
    {
        if (_bridgeRuntime?.Snapshot.State != BridgeRuntimeState.Running) return;
        string? generation = _sidecarAttestation?.IdentityGeneration;
        if (generation is null) return;
        foreach (FeishuDesktopSyncSubscription subscription in DesktopSyncSubscriptions.Pending())
        {
            token.ThrowIfCancellationRequested();
            FeishuProjectAuthorizationState authorization =
                _credentials.LoadProjectAuthorization(subscription.Sender);
            if (subscription.CreatedAt < DateTimeOffset.UtcNow.AddDays(-7)
                || subscription.Generation != generation
                || !_credentials.MatchesBoundOwner(subscription.Sender)
                || !authorization.IsAuthorized
                || !IsWithinAuthorizedProject(
                    Path.GetFullPath(authorization.ProjectPath),
                    subscription.WorkingDirectory))
            {
                DesktopSyncSubscriptions.Remove(subscription);
                continue;
            }

            CodexControlledRouteResult? terminal;
            if (subscription.ExpectedTurnId.Length > 0)
            {
                terminal = _currentReplyReader.ReadTerminalResult(
                    subscription.ThreadId,
                    subscription.ExpectedTurnId);
            }
            else
            {
                CodexDesktopSyncSnapshot snapshot = _currentReplyReader.ReadDesktopSyncSnapshot(
                    subscription.ThreadId);
                if (snapshot.Status == "running" && snapshot.TurnId.Length > 0)
                {
                    DesktopSyncSubscriptions.Save(subscription with
                    {
                        ExpectedTurnId = snapshot.TurnId,
                    });
                    continue;
                }
                terminal = snapshot.TurnId.Length > 0
                    && snapshot.TurnId != subscription.BaselineCompletedTurnId
                        ? snapshot.TerminalResult(subscription.ThreadId)
                        : null;
            }
            if (terminal is null) continue;

            await QueueDesktopSyncResultAsync(
                subscription.Generation,
                subscription.Sender,
                subscription.Title,
                subscription.WorkingDirectory,
                terminal,
                token);
            SaveReplyCursor(subscription.Generation + ":" + subscription.Sender + ":" + subscription.ThreadId, terminal.TurnId);
            DesktopSyncSubscriptions.Remove(subscription);
        }
    }

    private async Task SendStoredTaskResultAsync(FeishuResultEnvelope result, FeishuResultPart part, CancellationToken token)
    {
        if (!MayDeliverTaskResult(result)) throw new InvalidOperationException("原结果投递授权已失效。");
        if (part.ResourceFile is null)
        {
            await SendP2PMessageAsync(result.Sender, part.Text, part.Key, token);
            UpdateSnapshot(snapshot => snapshot with { LastReadOnlyRouteReplyAt = DateTimeOffset.Now, ReadOnlyRouteError = null });
            return;
        }
        using var input = new FeishuAttachmentSession();
        string directory = Path.GetDirectoryName(input.NewDownloadPath())!;
        string path = await ResultResources.MaterializeAsync(part, directory, token);
        if (!MayDeliverTaskResult(result)) throw new InvalidOperationException("原结果投递授权已失效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(125));
        FeishuSidecarCommandResult sent = await SendSidecarCommandAsync(JsonSerializer.Serialize(new
        {
            type = "resource_send", request_id = NewRequestId(), recipient_id = result.Sender,
            path, file_name = part.FileName, resource_kind = part.ResourceKind, idempotency_key = part.Key,
        }), timeout.Token);
        if (!sent.Succeeded) throw new InvalidOperationException($"结果附件发送失败（{sent.ErrorStage}/{sent.ErrorCode}）；已保留快照，稍后自动重试。");
        UpdateSnapshot(snapshot => snapshot with { LastReadOnlyRouteReplyAt = DateTimeOffset.Now, ReadOnlyRouteError = null });
    }

    private void StartTaskResultWorker()
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_routeGates.SessionCancellation);
        try { Interlocked.Exchange(ref _taskResultWorker, lifetime)?.Cancel(); }
        catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            Task approvalObservation = new CodexDesktopApprovalObserver().ObserveAsync(
                () => Volatile.Read(ref _replyObservedTaskIds),
                (taskId, waiting) => _replyApprovalWaiting[taskId] = waiting, lifetime.Token);
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(130));
                        // A stale selector/subscription must not starve unrelated already-saved results.
                        async Task Stage(Func<Task> action)
                        {
                            try { await action(); }
                            catch (Exception exception) when (!timeout.IsCancellationRequested)
                            {
                                UpdateSnapshot(snapshot => snapshot with { ReadOnlyRouteError = exception is InvalidOperationException
                                    ? exception.Message : "部分后台处理未完成，记录已保留，将继续重试。" });
                            }
                        }
                        await Stage(() => PumpTaskInputsAsync(timeout.Token));
                        await Stage(() => ObserveDesktopSyncSubscriptionsAsync(timeout.Token));
                        await Stage(() => ObserveCurrentTaskReplyAsync(timeout.Token));
                        await Stage(() => TaskResultOutbox.FlushAsync(MayDeliverTaskResult, SendStoredTaskResultAsync, timeout.Token));
                    }
                    catch (Exception exception) when (!lifetime.IsCancellationRequested)
                    {
                        // The encrypted outbox retains unsent parts for this identity's next attempt.
                        UpdateSnapshot(snapshot => snapshot with { ReadOnlyRouteError = exception is InvalidOperationException
                            ? exception.Message : "结果补发尚未完成，已保留待发内容，稍后重试。" });
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { await approvalObservation; } catch (OperationCanceledException) { }
                Interlocked.CompareExchange(ref _taskResultWorker, null, lifetime);
                lifetime.Dispose();
            }
        });
    }

    private async Task ObserveCurrentTaskReplyAsync(CancellationToken token)
    {
        if (_bridgeRuntime?.Snapshot.State != BridgeRuntimeState.Running) return;
        string? generation = _sidecarAttestation?.IdentityGeneration;
        if (generation is null) return;
        string? activeOwner = _credentials.ActiveBoundOwnerId();
        CodexThreadSummary? current = await _bridgeRuntime.GetCurrentThreadAsync(token);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        await FeishuSubscriptionObservation.ForEachOwnerAsync(_credentials.BoundOwnerIds(), async sender =>
        {
            // The global desktop selection belongs only to its active owner;
            // explicit subscriptions are independently observed for every owner.
            if (current is not null && sender == activeOwner)
            {
                observed.Add(current.ThreadId);
                await ObserveTaskReplyAsync(current, sender, generation, token);
            }
            foreach (var subscription in TaskSubscriptions.Read(SubscriptionScope(sender)))
            {
                if (sender == activeOwner && subscription.Key == current?.ThreadId) continue;
                CodexThreadSummary? task = await _bridgeRuntime.GetQueuedThreadAsync(subscription.Value, token);
                if (task is not null)
                {
                    observed.Add(task.ThreadId);
                    await ObserveTaskReplyAsync(task, sender, generation, token);
                }
            }
        }, () => UpdateSnapshot(snapshot => snapshot with
        { ReadOnlyRouteError = "部分使用者订阅暂不可读，原记录已保留；其他使用者继续处理。" }), token);
        Volatile.Write(ref _replyObservedTaskIds, observed.ToArray());
    }

    private async Task ObserveTaskReplyAsync(CodexThreadSummary current, string sender, string generation, CancellationToken token)
    {
        string scope = generation + ":" + sender + ":" + current.ThreadId;
        CodexDesktopSyncSnapshot snapshot = _currentReplyReader.ReadDesktopSyncSnapshot(current.ThreadId);
        if (snapshot.Status == "running" && (_replyApprovalWaiting.GetValueOrDefault(current.ThreadId)
            || current.ActiveFlags.Any(flag => flag is "waitingOnApproval" or "waitingOnUserInput")))
        {
            string gateId = $"gate:{generation}:{sender}:{current.ThreadId}:{snapshot.TurnId}";
            var gateEnvelope = new FeishuResultEnvelope(generation, sender, current.WorkingDirectory,
                [new FeishuResultPart($"Task：{SafeCardText(current.Title, 100)} · 等待你处理\n\n请在 Codex Desktop 查看并处理确认请求；桥接没有代你批准。",
                    BuildIdempotencyKey(gateId, "approval-needed"))]);
            if (MayDeliverTaskResult(gateEnvelope)) await TaskResultOutbox.SaveAsync(gateId, gateEnvelope, token);
        }
        if (!_observedCurrentReplies.TryGetValue(scope, out string? previous))
        {
            previous = ReplyCursors.Read(scope);
            if (previous is null)
            {
                SaveReplyCursor(scope, _currentReplyReader.ReadTerminalResultsAfter(current.ThreadId, null).LastOrDefault()?.TurnId ?? "");
                return;
            }
        }
        foreach (CodexControlledRouteResult result in _currentReplyReader.ReadTerminalResultsAfter(current.ThreadId, previous))
        {
        string turnId = result.TurnId;
        if (_bridgeTurnIds.TryGetValue(turnId, out string? originSender) && originSender == sender)
        {
            SaveReplyCursor(scope, turnId);
            continue; // The originating message's feedback owns delivery in this session.
        }
        string id = $"turn:{generation}:{sender}:{current.ThreadId}:{turnId}";
        string label = result.Status == "completed" ? "已完成" : result.Status == "interrupted" ? "已停止" : "运行失败";
        string text = string.IsNullOrWhiteSpace(result.AgentMessageText)
            ? $"任务{label}，本轮没有文字结果，请在 Codex Desktop 查看。" : result.AgentMessageText;
        string[] chunks = FeishuTaskFeedback.SplitUtf8(text).ToArray();
        string title = current.Title.Length > 100 ? current.Title[..100] : current.Title;
        var envelope = new FeishuResultEnvelope(generation, sender, current.WorkingDirectory,
            chunks.Select((chunk, index) => new FeishuResultPart(
                $"Task：{title} · {label}{(chunks.Length == 1 ? "" : $"（{index + 1}/{chunks.Length}）")}\n\n{chunk}",
                BuildIdempotencyKey(id, $"task-result-{index}"))).ToArray());
        if (!MayDeliverTaskResult(envelope)) return;
        FeishuResultPart[] resources = await ResultResources.CaptureAsync(id, result.AgentMessageText + "\n"
            + _currentReplyReader.ReadGeneratedImageLinks(result.ThreadId, result.TurnId), current.WorkingDirectory,
            index => BuildIdempotencyKey(id, $"task-resource-{index}"), token);
        envelope = envelope with { Parts = envelope.Parts.Concat(resources).ToArray() };
        await TaskResultOutbox.SaveAsync(id, envelope, token);
        SaveReplyCursor(scope, turnId);
        }
    }

    private async Task ExecuteAutomaticReadOnlyRouteAsync()
    {
        BridgeRuntime runtime = _bridgeRuntime
            ?? throw new InvalidOperationException("自动只读路由运行时不可用。");
        FeishuRouteAuthorization authorization;
        try
        {
            authorization = ClaimReadOnlyRouteExecution();
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteExecuting = true,
                ReadOnlyRouteError = null,
            });
        }
        catch (Exception exception)
        {
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteError = exception is InvalidOperationException
                    ? exception.Message
                    : $"自动只读路由 claim 失败：{exception.GetType().Name}",
                ReadOnlyRouteExecuting = false,
            });
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            authorization.SessionCancellation);
        try
        {
            if (runtime.Snapshot.State != BridgeRuntimeState.Running)
            {
                await runtime.StartAsync(linked.Token);
            }

            string prompt = authorization.Prompt?.Trim() ?? string.Empty;
            if (prompt.Length == 0)
            {
                throw new InvalidOperationException("自动只读路由没有有效提示词。");
            }

            await TrySetPromLightStatusAsync(PromLightStatus.Running);
            CodexControlledRouteResult result = await runtime.RunReadOnlyPromptAsync(
                prompt,
                authorization.ProjectPath,
                linked.Token);
            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.AgentMessageText))
            {
                throw new InvalidOperationException("Codex 没有返回可发送的完成文本。" );
            }

            await ReplyToCapturedReadOnlyRouteAsync(
                authorization,
                result.AgentMessageText,
                linked.Token);
            await TrySetPromLightStatusAsync(PromLightStatus.Idle);
        }
        catch (OperationCanceledException)
        {
            await TrySetPromLightStatusAsync(PromLightStatus.Error);
            _routeGates.Abort(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteError = "自动只读路由超时或会话已失效；没有继续发送回复。",
                ReadOnlyRouteExecuting = false,
            });
        }
        catch (Exception exception)
        {
            await TrySetPromLightStatusAsync(PromLightStatus.Error);
            _routeGates.Abort(authorization);
            UpdateSnapshot(snapshot => snapshot with
            {
                ReadOnlyRouteError = exception is InvalidOperationException
                    ? exception.Message
                    : $"自动只读路由失败：{exception.GetType().Name}",
                ReadOnlyRouteExecuting = false,
            });
        }
    }

    private async Task TrySetPromLightStatusAsync(PromLightStatus status)
    {
        if (_promLight is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _promLight.SetTransientStatusAsync(status, timeout.Token);
        }
        catch
        {
            // Task delivery remains authoritative; an unavailable indicator must not block it.
        }
    }

    private async Task ReplyToMessageAsync(
        string messageId,
        string replyText,
        string idempotencyPurpose,
        CancellationToken cancellationToken)
    {
        Process consumer = Volatile.Read(ref _consumer) is { HasExited: false } running
            ? running
            : throw new InvalidOperationException("飞书官方 SDK sidecar 不在运行；没有发送回复。");
        if (Snapshot.State != FeishuEventRuntimeState.Listening)
        {
            throw new InvalidOperationException("飞书连接不在 Listening 状态；没有发送回复。");
        }

        string requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<FeishuSidecarCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法登记飞书 SDK 回复命令。");
        }

        try
        {
            string command = FeishuSidecarProtocol.BuildReplyCommandJson(
                requestId,
                messageId,
                replyText,
                BuildIdempotencyKey(messageId, idempotencyPurpose));
            await _consumerInputLock.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref _consumer), consumer)
                    || consumer.HasExited)
                {
                    throw new InvalidOperationException("飞书官方 SDK sidecar 已更换或退出；没有发送回复。");
                }

                await consumer.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
                await consumer.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _consumerInputLock.Release();
            }

            FeishuSidecarCommandResult result = await completion.Task.WaitAsync(cancellationToken);
            if (!result.Succeeded)
            {
                string code = result.ErrorCode?.ToString() ?? "不可用";
                throw new InvalidOperationException(
                    $"飞书回复失败：阶段={result.ErrorStage}；错误码={code}。未输出敏感诊断。");
            }
        }
        finally
        {
            _pendingCommands.TryRemove(requestId, out _);
        }
    }

    internal static string BuildIdempotencyKey(string messageId, string purpose)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            purpose + "\0" + messageId));
        return "dbw-" + Convert.ToHexString(digest)[..32].ToLowerInvariant();
    }

    private FeishuRouteAuthorization ClaimRouteExecution(FeishuRouteKind kind)
    {
        FeishuRouteAuthorization authorization = FeishuRouteClaimProjection.ClaimAndProject(
            () =>
            {
                if (Snapshot.State != FeishuEventRuntimeState.Listening)
                {
                    throw new InvalidOperationException(
                        "飞书连接不在 Listening 状态；人工 Gate 已失效。");
                }

                return _routeGates.Claim(kind, DateTimeOffset.Now);
            },
            () => _routeGates.State,
            (gates, failure) =>
            {
                UpdateSnapshot(snapshot =>
                {
                    snapshot = ApplyRouteGateState(snapshot, gates);
                    string? error = failure is InvalidOperationException
                        ? failure.Message
                        : failure is null
                            ? null
                            : "人工 Gate claim 失败；未执行任何操作。";
                    return kind == FeishuRouteKind.Controlled
                        ? snapshot with { CodexRouteError = error }
                        : snapshot with { ReadOnlyRouteError = error };
                });
            });
        _armExpiryScheduler.Cancel(kind);
        return authorization;
    }

    private static FeishuEventRuntimeSnapshot ApplyRouteGateState(
        FeishuEventRuntimeSnapshot snapshot,
        FeishuRouteGateState gates) => snapshot with
    {
        OwnerBound = gates.OwnerBound,
        CodexRouteCaptureArmed = gates.ControlledArmed,
        CodexRouteCapturePending = gates.ControlledPending && !gates.ControlledClaimed,
        ReadOnlyRouteCaptureArmed = gates.ReadOnlyArmed,
        ReadOnlyRouteCapturePending = gates.ReadOnlyPending && !gates.ReadOnlyClaimed,
        ReadOnlyPromptPreview = gates.ReadOnlyPrompt,
        RegisteredConsumerCount = snapshot.State == FeishuEventRuntimeState.Listening
            ? snapshot.RegisteredConsumerCount
            : 0,
        ExpectedConsumerCount = FeishuSidecarProtocol.ExpectedConsumers.Length,
    };

    private void FailPendingCommands()
    {
        foreach (TaskCompletionSource<FeishuSidecarCommandResult> completion in _pendingCommands.Values)
        {
            completion.TrySetException(
                new IOException("飞书官方 SDK sidecar 已结束；回复状态未确认。"));
        }
    }

    private void EnsureProjectAuthorized()
    {
        if (!_credentials.HasAnyProjectAuthorization())
        {
            throw new InvalidOperationException("请先为至少一位已绑定用户保存一个明确存在的 Codex 项目目录；不会默认开放所有项目。");
        }
    }

    private void SetSnapshot(FeishuEventRuntimeSnapshot snapshot)
    {
        snapshot = _snapshotStore.Replace(snapshot);
        StateChanged?.Invoke(snapshot);
    }

    private void UpdateSnapshot(
        Func<FeishuEventRuntimeSnapshot, FeishuEventRuntimeSnapshot> update)
    {
        FeishuEventRuntimeSnapshot snapshot = _snapshotStore.Update(update);
        StateChanged?.Invoke(snapshot);
    }

    private static TaskCompletionSource NewReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string SafeError(Exception exception) => exception switch
    {
        TimeoutException => exception.Message,
        FileNotFoundException => exception.Message,
        InvalidOperationException => exception.Message,
        _ => $"飞书长连接失败：{exception.GetType().Name}",
    };

}
