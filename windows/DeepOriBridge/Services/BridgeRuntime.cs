using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class BridgeRuntime : IAsyncDisposable
{
    public const string ControlledRoutePrompt = "请仅回复：Windows Codex 路由测试已完成。";
    public const string ControlledRouteExpectedResponse = "Windows Codex 路由测试已完成。";
    public const int ReadOnlyPromptMaxLength = 4000;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _controlledRouteLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private CodexAppServerClient? _client;
    private BridgeRuntimeSnapshot _snapshot = BridgeRuntimeSnapshot.Initial;
    private string _activeTurnId = string.Empty;
    private int _activeTurnStarted;

    public event Action<BridgeRuntimeSnapshot>? StateChanged;

    public BridgeRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    internal int? AppServerProcessId => _client?.ProcessId;

    internal bool LastInterruptAcknowledged { get; private set; }

    internal string LastCancellationTerminalStatus { get; private set; } = string.Empty;

    internal string ActiveTurnId => Volatile.Read(ref _activeTurnId);

    internal bool ActiveTurnStarted => Volatile.Read(ref _activeTurnStarted) != 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_client?.IsConnected == true)
            {
                await RefreshCoreAsync(_client, cancellationToken);
                return;
            }

            CodexAppServerClient? staleClient = _client;
            _client = null;
            if (staleClient is not null)
            {
                staleClient.NotificationReceived -= OnNotificationReceived;
                staleClient.Disconnected -= OnDisconnected;
                await staleClient.DisposeAsync();
            }

            Publish(Snapshot with
            {
                State = BridgeRuntimeState.Starting,
                Error = null,
                UpdatedAt = DateTimeOffset.Now,
            });

            string executable = CodexExecutableLocator.Find();
            if (string.IsNullOrWhiteSpace(executable))
            {
                throw new FileNotFoundException("未找到 Codex Desktop 随附的 codex.exe。");
            }

            var client = new CodexAppServerClient();
            client.NotificationReceived += OnNotificationReceived;
            client.Disconnected += OnDisconnected;
            try
            {
                await client.ConnectAsync(executable, cancellationToken);
                CodexThreadListResult threads = await client.ListThreadsAsync(
                    limit: 1,
                    useStateDatabaseOnly: true,
                    cancellationToken);
                _client = client;
                Publish(new BridgeRuntimeSnapshot(
                    BridgeRuntimeState.Running,
                    client.SessionInfo?.Platform ?? string.Empty,
                    threads.Count,
                    0,
                    string.Empty,
                    null,
                    DateTimeOffset.Now));
            }
            catch
            {
                client.NotificationReceived -= OnNotificationReceived;
                client.Disconnected -= OnDisconnected;
                await client.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            Publish(BridgeRuntimeSnapshot.Initial with { UpdatedAt = DateTimeOffset.Now });
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(Snapshot with
            {
                State = BridgeRuntimeState.Faulted,
                Error = exception.Message,
                UpdatedAt = DateTimeOffset.Now,
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_client?.IsConnected != true)
            {
                throw new InvalidOperationException("只读桥接会话尚未启动。");
            }

            await RefreshCoreAsync(_client, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CodexAppServerClient? failedClient = _client;
            _client = null;
            if (failedClient is not null)
            {
                failedClient.NotificationReceived -= OnNotificationReceived;
                failedClient.Disconnected -= OnDisconnected;
                await failedClient.DisposeAsync();
            }

            Publish(Snapshot with
            {
                State = BridgeRuntimeState.Faulted,
                Error = exception.Message,
                UpdatedAt = DateTimeOffset.Now,
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            Publish(Snapshot with
            {
                State = BridgeRuntimeState.Stopping,
                Error = null,
                UpdatedAt = DateTimeOffset.Now,
            });

            CodexAppServerClient? client = _client;
            _client = null;
            if (client is not null)
            {
                client.NotificationReceived -= OnNotificationReceived;
                client.Disconnected -= OnDisconnected;
                await client.DisposeAsync();
            }

            Publish(new BridgeRuntimeSnapshot(
                BridgeRuntimeState.Stopped,
                string.Empty,
                0,
                0,
                string.Empty,
                null,
                DateTimeOffset.Now));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CodexControlledRouteResult> RunControlledRouteTestAsync(
        CancellationToken cancellationToken)
    {
        return await RunReadOnlyPromptAsync(
            ControlledRoutePrompt,
            Directory.GetCurrentDirectory(),
            cancellationToken);
    }

    public async Task<CodexControlledRouteResult> RunControlledRouteTestAsync(
        string authorizedWorkingDirectory,
        CancellationToken cancellationToken)
    {
        return await RunReadOnlyPromptAsync(
            ControlledRoutePrompt,
            authorizedWorkingDirectory,
            cancellationToken);
    }

    public async Task<CodexControlledRouteResult> RunReadOnlyPromptAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        return await RunReadOnlyPromptAsync(
            prompt,
            Directory.GetCurrentDirectory(),
            cancellationToken);
    }

    public async Task<CodexControlledRouteResult> RunReadOnlyPromptAsync(
        string prompt,
        string authorizedWorkingDirectory,
        CancellationToken cancellationToken)
    {
        string approvedPrompt = prompt?.Trim()
            ?? throw new ArgumentNullException(nameof(prompt));
        if (approvedPrompt.Length == 0)
        {
            throw new ArgumentException("只读提示词不能为空。", nameof(prompt));
        }

        if (approvedPrompt.Length > ReadOnlyPromptMaxLength)
        {
            throw new ArgumentException(
                $"只读提示词不能超过 {ReadOnlyPromptMaxLength} 个字符。",
                nameof(prompt));
        }

        string workingDirectory = Path.GetFullPath(authorizedWorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("已授权的 Codex 项目目录不存在。");
        }

        await _controlledRouteLock.WaitAsync(cancellationToken);
        try
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
            CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                ? connectedClient
                : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
            LastInterruptAcknowledged = false;
            LastCancellationTerminalStatus = string.Empty;
            Volatile.Write(ref _activeTurnId, string.Empty);
            Volatile.Write(ref _activeTurnStarted, 0);

            CodexThreadStartResult thread = await client.StartThreadAsync(
                workingDirectory,
                cancellationToken);
            var completion = new TaskCompletionSource<CodexControlledRouteResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string? startedTurnId = null;

            void ObserveCompletion(CodexAppServerNotification notification)
            {
                if (notification.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !notification.Parameters.TryGetProperty("threadId", out var threadIdElement)
                    || !string.Equals(
                        threadIdElement.GetString(),
                        thread.ThreadId,
                        StringComparison.Ordinal)
                    || !notification.Parameters.TryGetProperty("turn", out var turn)
                    || turn.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return;
                }

                string turnId = turn.TryGetProperty("id", out var turnIdElement)
                    ? turnIdElement.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(startedTurnId)
                    && !string.Equals(turnId, startedTurnId, StringComparison.Ordinal))
                {
                    return;
                }

                if (string.Equals(notification.Method, "turn/started", StringComparison.Ordinal))
                {
                    Volatile.Write(ref _activeTurnStarted, 1);
                    return;
                }

                if (!string.Equals(notification.Method, "turn/completed", StringComparison.Ordinal))
                {
                    return;
                }

                string status = turn.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;
                string agentMessage = ReadLastAgentMessage(turn);
                completion.TrySetResult(new CodexControlledRouteResult(
                    thread.ThreadId,
                    turnId,
                    status,
                    agentMessage));
            }

            void ObserveDisconnect(Exception exception)
            {
                completion.TrySetException(new IOException(
                    "Codex app-server stdio 在 turn/completed 前断开。",
                    exception));
            }

            client.NotificationReceived += ObserveCompletion;
            client.Disconnected += ObserveDisconnect;
            try
            {
                CodexTurnStartResult startedTurn;
                try
                {
                    startedTurn = await client.StartTurnAsync(
                        thread.ThreadId,
                        approvedPrompt,
                        cancellationToken);
                }
                catch
                {
                    await FaultClientSessionAsync(
                        client,
                        "Codex turn/start 未能确认 turnId；已关闭 app-server 会话以排除孤儿回合。");
                    LastCancellationTerminalStatus = "session-faulted";
                    throw;
                }

                startedTurnId = startedTurn.TurnId;
                Volatile.Write(ref _activeTurnId, startedTurn.TurnId);
                CodexControlledRouteResult result;
                try
                {
                    result = await completion.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    bool terminated = await TryInterruptAndConfirmTerminalAsync(
                        client,
                        thread.ThreadId,
                        startedTurn.TurnId,
                        completion.Task);
                    if (!terminated)
                    {
                        await FaultClientSessionAsync(
                            client,
                            "Codex turn 取消后未能确认终态；已关闭 app-server 会话以排除孤儿回合。");
                        LastCancellationTerminalStatus = "session-faulted";
                    }

                    throw;
                }
                catch
                {
                    await FaultClientSessionAsync(
                        client,
                        "Codex turn 完成前连接异常；已关闭 app-server 会话以排除孤儿回合。");
                    throw;
                }

                if (!string.Equals(result.TurnId, startedTurn.TurnId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Codex 完成通知与已启动的 turn 不一致。");
                }

                return result;
            }
            finally
            {
                client.NotificationReceived -= ObserveCompletion;
                client.Disconnected -= ObserveDisconnect;
                Volatile.Write(ref _activeTurnId, string.Empty);
                Volatile.Write(ref _activeTurnStarted, 0);
            }
            }
            finally
            {
                _operationLock.Release();
            }
        }
        finally
        {
            _controlledRouteLock.Release();
        }
    }

    private async Task<bool> TryInterruptAndConfirmTerminalAsync(
        CodexAppServerClient client,
        string threadId,
        string turnId,
        Task<CodexControlledRouteResult> completion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool interruptAcknowledged = false;
        try
        {
            await client.InterruptTurnAsync(threadId, turnId, timeout.Token);
            interruptAcknowledged = true;
            LastInterruptAcknowledged = true;
        }
        catch
        {
            // A terminal notification may have won the race with turn/interrupt.
        }

        try
        {
            CodexControlledRouteResult terminal = await completion.WaitAsync(timeout.Token);
            LastCancellationTerminalStatus = terminal.Status;
            return string.Equals(terminal.TurnId, turnId, StringComparison.Ordinal)
                && CodexAppServerClient.IsTerminalTurnStatus(terminal.Status)
                && (interruptAcknowledged
                    || string.Equals(terminal.Status, "completed", StringComparison.Ordinal)
                    || string.Equals(terminal.Status, "failed", StringComparison.Ordinal)
                    || string.Equals(terminal.Status, "interrupted", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private async Task FaultClientSessionAsync(
        CodexAppServerClient client,
        string safeReason)
    {
        if (ReferenceEquals(_client, client))
        {
            _client = null;
        }

        client.NotificationReceived -= OnNotificationReceived;
        client.Disconnected -= OnDisconnected;
        await client.DisposeAsync();
        Publish(Snapshot with
        {
            State = BridgeRuntimeState.Faulted,
            Error = safeReason,
            UpdatedAt = DateTimeOffset.Now,
        });
    }

    private static string ReadLastAgentMessage(System.Text.Json.JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var items)
            || items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return string.Empty;
        }

        string text = string.Empty;
        foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                && item.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), "agentMessage", StringComparison.Ordinal)
                && item.TryGetProperty("text", out var textElement))
            {
                text = textElement.GetString() ?? string.Empty;
            }
        }

        return text;
    }

    private async Task RefreshCoreAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        CodexThreadListResult threads = await client.ListThreadsAsync(
            limit: 1,
            useStateDatabaseOnly: true,
            cancellationToken);
        BridgeRuntimeSnapshot current = Snapshot;
        Publish(current with
        {
            State = BridgeRuntimeState.Running,
            Platform = client.SessionInfo?.Platform ?? current.Platform,
            ThreadCount = threads.Count,
            Error = null,
            UpdatedAt = DateTimeOffset.Now,
        });
    }

    private void OnNotificationReceived(CodexAppServerNotification notification)
    {
        BridgeRuntimeSnapshot current = Snapshot;
        if (current.State != BridgeRuntimeState.Running)
        {
            return;
        }

        Publish(current with
        {
            NotificationCount = current.NotificationCount + 1,
            LastNotificationMethod = notification.Method,
            UpdatedAt = notification.ReceivedAt,
        });
    }

    private void OnDisconnected(Exception exception)
    {
        BridgeRuntimeSnapshot current = Snapshot;
        if (current.State is BridgeRuntimeState.Stopped or BridgeRuntimeState.Stopping)
        {
            return;
        }

        Publish(current with
        {
            State = BridgeRuntimeState.Faulted,
            Error = exception.Message,
            UpdatedAt = DateTimeOffset.Now,
        });
    }

    private void Publish(BridgeRuntimeSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }

        StateChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _operationLock.Dispose();
        _controlledRouteLock.Dispose();
    }
}
