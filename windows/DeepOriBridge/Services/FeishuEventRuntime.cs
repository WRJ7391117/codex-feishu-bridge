using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

public sealed class FeishuEventRuntime : IAsyncDisposable
{
    private const string MessageEventKey = "im.message.receive_v1";
    private const string ApprovedTriggerText = "Windows Bridge 回执测试";
    private const string ApprovedReplyText = "DeepOri Bridge for Windows 已收到测试消息。";
    private const string OwnerBindingTriggerText = "Windows Bridge 绑定所有者";
    private const string CodexRouteTriggerText = "Windows Codex 路由测试";
    private const string CodexRouteReplyText = "DeepOri Bridge for Windows 已完成 Codex 路由测试。";
    private const string ReadOnlyRoutePrefix = "Windows Codex：";
    private const int ReadOnlyReplyMaxLength = 4000;
    private const string TruncatedReplySuffix = "\n\n[输出已截断]";

    private readonly FeishuCredentialService _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _consumerInputLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingReplyCommands = new();
    private readonly FeishuRouteGateCoordinator _routeGates = new();
    private readonly FeishuEventSnapshotStore _snapshotStore;
    private Process? _consumer;
    private TaskCompletionSource _ready = NewReadySource();
    private bool _stopRequested;
    private int _replyArmed;
    private int _ownerBindingArmed;
    private int _disposeStarted;
    private readonly FeishuArmExpiryScheduler _armExpiryScheduler = new();

    public FeishuEventRuntime(FeishuCredentialService credentials)
    {
        _credentials = credentials;
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

        if (current.OwnerBound)
        {
            throw new InvalidOperationException("owner 已绑定；如需更换，请先显式撤销现有绑定。");
        }

        Interlocked.Exchange(ref _ownerBindingArmed, 1);
        UpdateSnapshot(snapshot => snapshot with
        {
            OwnerBindingArmed = true,
            OwnerBindingError = null,
        });
    }

    public void ClearOwnerBinding()
    {
        _armExpiryScheduler.CancelAll();
        _credentials.ClearOwnerBinding();
        Interlocked.Exchange(ref _replyArmed, 0);
        Interlocked.Exchange(ref _ownerBindingArmed, 0);
        _routeGates.Invalidate(
            ownerBound: false,
            listening: Snapshot.State == FeishuEventRuntimeState.Listening);
        UpdateSnapshot(snapshot => snapshot with
        {
            OwnerBound = false,
            ReplyArmed = false,
            OwnerBindingArmed = false,
            OwnerBindingError = null,
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
            });
            throw;
        }
    }

    internal void AbortRouteExecution(FeishuRouteAuthorization authorization)
    {
        _routeGates.Abort(authorization);
        UpdateSnapshot(snapshot => snapshot);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        await _gate.WaitAsync(cancellationToken);
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

            (string AppId, string Secret)? runtimeCredential = _credentials.LoadForRuntime();
            if (runtimeCredential is null)
            {
                throw new InvalidOperationException("请先保存并验证飞书测试机器人凭证。");
            }

            _stopRequested = false;
            _armExpiryScheduler.CancelAll();
            _ready = NewReadySource();
            Interlocked.Exchange(ref _replyArmed, 0);
            Interlocked.Exchange(ref _ownerBindingArmed, 0);
            bool ownerBound = _credentials.LoadOwnerBinding().IsBound;
            _routeGates.BeginSession(ownerBound);
            SetSnapshot(ApplyRouteGateState(new FeishuEventRuntimeSnapshot(
                FeishuEventRuntimeState.Connecting,
                OwnerBound: ownerBound), _routeGates.State));
            StartEventSidecar(runtimeCredential.Value.AppId, runtimeCredential.Value.Secret);

            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await _ready.Task.WaitAsync(readyTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("飞书 WebSocket 长连接没有在 20 秒内进入 ready 状态。");
            }
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
            });
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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
            Interlocked.Exchange(ref _ownerBindingArmed, 0);
            _routeGates.Invalidate(
                _credentials.LoadOwnerBinding().IsBound,
                listening: false);
            UpdateSnapshot(snapshot => snapshot with
            {
                State = FeishuEventRuntimeState.Stopping,
                Error = null,
                ReplyArmed = false,
                OwnerBindingArmed = false,
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
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _replyArmed, 0);
        Interlocked.Exchange(ref _ownerBindingArmed, 0);
        _routeGates.Invalidate(
            _credentials.LoadOwnerBinding().IsBound,
            listening: false);
        UpdateSnapshot(snapshot => snapshot with
        {
            ReplyArmed = false,
            OwnerBindingArmed = false,
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
        _routeGates.Dispose();
    }

    private void OnConsumerOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        ProcessConsumerOutput(sender as Process, eventArgs.Data ?? string.Empty);
    }

    private void StartEventSidecar(string appId, string appSecret)
    {
        string sidecarPath = Path.Combine(
            AppContext.BaseDirectory,
            "Tools",
            "feishu-event-sidecar.exe");
        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException("飞书官方 SDK sidecar 未随应用部署。", sidecarPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sidecarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        Interlocked.Exchange(ref _consumer, consumer);
        consumer.BeginOutputReadLine();
        consumer.BeginErrorReadLine();
        string startupJson = JsonSerializer.Serialize(new
        {
            app_id = appId,
            app_secret = appSecret,
        });
        consumer.StandardInput.WriteLine(startupJson);
        consumer.StandardInput.Flush();
    }

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
                out bool succeeded))
            {
                if (_pendingReplyCommands.TryRemove(
                    requestId,
                    out TaskCompletionSource<bool>? completion))
                {
                    completion.TrySetResult(succeeded);
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
                    _routeGates.SetListening(true);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Listening,
                        Error = null,
                    });
                    _ready.TrySetResult();
                }
                else if (string.Equals(
                    lifecycleState,
                    "reconnecting",
                    StringComparison.Ordinal))
                {
                    _armExpiryScheduler.CancelAll();
                    _routeGates.SetListening(false);
                    Interlocked.Exchange(ref _replyArmed, 0);
                    Interlocked.Exchange(ref _ownerBindingArmed, 0);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Connecting,
                        Error = null,
                        ReplyArmed = false,
                        OwnerBindingArmed = false,
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
                    Interlocked.Exchange(ref _ownerBindingArmed, 0);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        State = FeishuEventRuntimeState.Connecting,
                        Error = "飞书 WebSocket 已断开；所有人工 Gate 已清除，等待 SDK 重新连接。",
                        ReplyArmed = false,
                        OwnerBindingArmed = false,
                    });
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

            UpdateSnapshot(snapshot => snapshot with
            {
                ReceivedEventCount = snapshot.ReceivedEventCount + 1,
                LastReceivedAt = DateTimeOffset.Now,
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
            string approvedAction =
                FeishuEventSafetyPolicy.MatchesFixedCommand(content, ApprovedTriggerText)
                    ? "reply-test"
                    : FeishuEventSafetyPolicy.MatchesFixedCommand(content, OwnerBindingTriggerText)
                        ? "owner-bind"
                        : FeishuEventSafetyPolicy.MatchesFixedCommand(content, CodexRouteTriggerText)
                            ? "codex-route-test"
                            : content is not null
                                && content.StartsWith(ReadOnlyRoutePrefix, StringComparison.Ordinal)
                                    ? "codex-readonly"
                                    : "unmatched";
            string? readOnlyPrompt = string.Equals(
                approvedAction,
                "codex-readonly",
                StringComparison.Ordinal)
                    ? content![ReadOnlyRoutePrefix.Length..]
                    : null;

            bool isP2p = string.Equals(chatType, "p2p", StringComparison.Ordinal);
            bool isUser = string.Equals(senderType, "user", StringComparison.Ordinal);
            bool isText = string.Equals(messageType, "text", StringComparison.Ordinal)
                || string.Equals(messageType, "post", StringComparison.Ordinal);
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
            bool isReplyTest = string.Equals(approvedAction, "reply-test", StringComparison.Ordinal);
            bool isCodexRouteTest = string.Equals(
                approvedAction,
                "codex-route-test",
                StringComparison.Ordinal);
            bool isReadOnlyRoute = string.Equals(
                approvedAction,
                "codex-readonly",
                StringComparison.Ordinal);

            _routeGates.SetOwnerBound(ownerBound);
            UpdateSnapshot(snapshot => snapshot with
            {
                OwnerBound = ownerBound,
                LastMatchDiagnostic = $"最近事件安全匹配：应用动作标签={approvedAction}；单聊={YesNo(isP2p)}；用户={YesNo(isUser)}；文本类型={YesNo(isText)}；消息引用={YesNo(hasMessageId)}；owner={OwnerLabel(ownerBound, ownerMatches)}。",
            });

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
                ownerMatches,
                DateTimeOffset.Now);
            if (capture.Duplicate)
            {
                UpdateSnapshot(snapshot => snapshot with
                {
                    LastMatchDiagnostic = "最近事件安全匹配：重复消息引用已在内存去重；未重新捕获、执行或回复。",
                });
                return;
            }

            if (approvedTarget
                && isOwnerBind
                && Interlocked.CompareExchange(ref _ownerBindingArmed, 0, 1) == 1)
            {
                try
                {
                    _credentials.BindOwner(senderId!);
                    _routeGates.SetOwnerBound(true);
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        OwnerBound = true,
                        OwnerBindingArmed = false,
                        LastOwnerBoundAt = DateTimeOffset.Now,
                        OwnerBindingError = null,
                        LastMatchDiagnostic = $"最近事件安全匹配：CLI 动作标签=owner-bind；单聊=是；用户=是；文本类型=是；消息引用=是；owner=匹配。",
                    });
                }
                catch (Exception exception)
                {
                    UpdateSnapshot(snapshot => snapshot with
                    {
                        OwnerBindingArmed = false,
                        OwnerBindingError = $"owner 绑定失败：{exception.GetType().Name}",
                    });
                }
            }

            if (approvedTarget
                && isReplyTest
                && ownerMatches
                && Interlocked.CompareExchange(ref _replyArmed, 0, 1) == 1)
            {
                UpdateSnapshot(snapshot => snapshot with { ReplyArmed = false, ReplyError = null });
                _ = ReplyToApprovedMessageAsync(
                    messageId!,
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

        FailPendingReplyCommands();
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
        Interlocked.Exchange(ref _ownerBindingArmed, 0);
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
            });
            return;
        }

        UpdateSnapshot(snapshot => snapshot with
        {
            State = FeishuEventRuntimeState.Faulted,
            Error = $"飞书事件进程意外退出{(exitCode is null ? "" : $"（exit {exitCode}）")}。",
            ReplyArmed = false,
            OwnerBindingArmed = false,
        });
        _ready.TrySetException(new InvalidOperationException("飞书事件进程在 ready 前退出。"));
    }

    private async Task StopConsumerOnlyAsync()
    {
        Process? consumer = Interlocked.Exchange(ref _consumer, null);
        FailPendingReplyCommands();
        if (consumer is null)
        {
            return;
        }

        try
        {
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

    private async Task ReplyToApprovedMessageAsync(
        string messageId,
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
                ApprovedReplyText,
                "deepori-win-test",
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
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingReplyCommands.TryAdd(requestId, completion))
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

            bool succeeded = await completion.Task.WaitAsync(cancellationToken);
            if (!succeeded)
            {
                throw new InvalidOperationException("飞书官方 SDK sidecar 回复失败；敏感诊断未输出。");
            }
        }
        finally
        {
            _pendingReplyCommands.TryRemove(requestId, out _);
        }
    }

    private static string BuildIdempotencyKey(string messageId, string purpose)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return purpose + "-" + Convert.ToHexString(digest)[..24].ToLowerInvariant();
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
    };

    private void FailPendingReplyCommands()
    {
        foreach (TaskCompletionSource<bool> completion in _pendingReplyCommands.Values)
        {
            completion.TrySetException(
                new IOException("飞书官方 SDK sidecar 已结束；回复状态未确认。"));
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
