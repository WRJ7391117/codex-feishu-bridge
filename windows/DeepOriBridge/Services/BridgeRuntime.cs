using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class BridgeRuntime : IAsyncDisposable
{
    public const string ControlledRoutePrompt = "请仅回复：Windows Codex 路由测试已完成。";
    public const string ControlledRouteExpectedResponse = "Windows Codex 路由测试已完成。";
    public const int ReadOnlyPromptMaxLength = 4000;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _controlledRouteLock = new(1, 1);
    private readonly TaskExecutionGate _taskExecutionGate = new(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeTaskTurns = new(StringComparer.Ordinal);
    private readonly object _snapshotLock = new();
    private CodexAppServerClient? _client;
    private BridgeRuntimeSnapshot _snapshot = BridgeRuntimeSnapshot.Initial;
    private CodexUsageSnapshot _usageSnapshot = CodexUsageSnapshot.Initial;
    private string _activeTurnId = string.Empty;
    private int _activeTurnStarted;
    private CurrentTaskSelection _selection = CurrentTaskSelection.Empty;
    private readonly object _selectionLock = new();
    private readonly CurrentTaskSelectionStore? _selectionStore;
    private string? _selectionScope;

    internal BridgeRuntime(CurrentTaskSelectionStore? selectionStore = null) => _selectionStore = selectionStore;

    private CurrentTaskSelection ReadSelection()
    {
        lock (_selectionLock)
        {
            if (_selectionStore is not null)
            {
                string? scope = _selectionStore.Scope;
                if (scope != _selectionScope || scope is null)
                {
                    // Clear memory before loading so a failed read cannot retain another owner's route.
                    _selection = CurrentTaskSelection.Empty;
                    CurrentTaskSelection restored = scope is null
                        ? CurrentTaskSelection.Empty : _selectionStore.Load(scope);
                    _selection = restored;
                    _selectionScope = scope;
                }
            }
            return _selection;
        }
    }

    private void SetSelection(CurrentTaskSelection value, string? expectedScope = null)
    {
        lock (_selectionLock)
        {
            ReadSelection();
            if (_selectionStore is not null)
            {
                if (_selectionScope is null)
                    throw new InvalidOperationException("当前 Bot、owner 或项目授权无效；未保存 Task 选择。");
                if (expectedScope is not null && expectedScope != _selectionScope)
                    throw new InvalidOperationException("Task 选择期间授权已变化；未切换。");
                // Persist before acknowledging a successful switch to Feishu.
                _selectionStore.Save(_selectionScope, value);
            }
            _selection = value;
        }
    }

    public event Action<BridgeRuntimeSnapshot>? StateChanged;

    public event Action<CodexUsageSnapshot>? UsageChanged;

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

    public CodexUsageSnapshot UsageSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _usageSnapshot;
            }
        }
    }

    internal int? AppServerProcessId => _client?.ProcessId;

    internal bool LastInterruptAcknowledged { get; private set; }

    internal string LastCancellationTerminalStatus { get; private set; } = string.Empty;

    internal string ActiveTurnId => _activeTaskTurns.GetValueOrDefault(SelectedThreadId) ?? Volatile.Read(ref _activeTurnId);

    internal bool ActiveTurnStarted => !_activeTaskTurns.IsEmpty || Volatile.Read(ref _activeTurnStarted) != 0;

    internal string SelectedThreadId => ReadSelection().ThreadId;
    internal string SelectedDesktopProjectId => ReadSelection().DesktopProjectId;
    internal CurrentTaskSelection CaptureSelection() => ReadSelection();
    internal string CaptureAuthorizationScope() { ReadSelection(); return _selectionScope ?? ""; }

    internal async Task<CodexThreadSummary?> GetQueuedThreadAsync(CurrentTaskSelection selection, CancellationToken token)
    {
        // A queued request retains its original target, never the later menu selection.
        if (string.IsNullOrWhiteSpace(selection.ProjectRoot)) return null;
        var visible = new List<CodexThreadSummary>();
        if (!string.IsNullOrWhiteSpace(selection.DesktopProjectId))
            visible.AddRange(await GetDesktopProjectThreadsAsync(selection.DesktopProjectId, selection.ProjectRoot, token));
        else
            foreach (CodexDesktopProject project in await GetDesktopProjectsAsync(selection.ProjectRoot, token))
                visible.AddRange(await GetDesktopProjectThreadsAsync(project.Id, selection.ProjectRoot, token));
        return visible.FirstOrDefault(t => t.ThreadId == selection.ThreadId && !t.Archived && t.AuthorizedForRouting);
    }

    public async Task<IReadOnlyList<CodexDesktopProject>> GetDesktopProjectsAsync(
        string authorizedRoot, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ConnectedClient.ListDesktopProjectsAsync(authorizedRoot, cancellationToken);
        }
        finally { _operationLock.Release(); }
    }

    private CodexAppServerClient ConnectedClient => _client is { IsConnected: true } client
        ? client : throw new InvalidOperationException("Codex app-server 会话尚未运行。");

    public async Task<IReadOnlyList<CodexThreadSummary>> GetDesktopProjectThreadsAsync(
        string projectId, string authorizedRoot, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ConnectedClient.ListDesktopProjectThreadsAsync(projectId, authorizedRoot, cancellationToken);
        }
        finally { _operationLock.Release(); }
    }

    public async Task<CodexThreadSummary> SelectDesktopProjectThreadAsync(
        string projectId, string threadId, string authorizedRoot, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ReadSelection();
            string? expectedScope = _selectionScope;
            CodexThreadSummary selected = (await ConnectedClient.ListDesktopProjectThreadsAsync(
                projectId, authorizedRoot, cancellationToken))
                .SingleOrDefault(thread => thread.ThreadId == threadId)
                ?? throw new InvalidOperationException("该 Task 已移出所选项目或授权范围，未切换。");
            if (!selected.AuthorizedForRouting)
                throw new InvalidOperationException("该 Task 的工作目录尚未授权；未切换。请在 Bridge 中选择明确的项目目录。");
            // This operation only changes the routing selection, not the Task's execution/archive state.
            SetSelection(new(selected.ThreadId, Path.GetFullPath(authorizedRoot), projectId), expectedScope);
            return selected;
        }
        finally { _operationLock.Release(); }
    }

    public async Task<CodexThreadSummary?> GetCurrentThreadAsync(CancellationToken cancellationToken)
    {
        CurrentTaskSelection selection = ReadSelection();
        string selected = selection.ThreadId;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }
        CodexThreadSummary? current = await GetQueuedThreadAsync(selection, cancellationToken);
        if (ReadSelection() != selection) return null;
        if (current is null)
        {
            if (ReadSelection() == selection) SetSelection(CurrentTaskSelection.Empty);
        }
        return current;
    }

    public async Task<CodexThreadSummary?> SwitchToNextThreadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexThreadSummary> threads = await GetRecentThreadsAsync(cancellationToken);
        if (threads.Count == 0) return null;
        string selected = SelectedThreadId;
        int currentIndex = -1;
        for (int index = 0; index < threads.Count; index++)
        {
            if (string.Equals(threads[index].ThreadId, selected, StringComparison.Ordinal))
            {
                currentIndex = index;
                break;
            }
        }
        CodexThreadSummary next = threads[(currentIndex + 1) % threads.Count];
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        await client.ResumeThreadAsync(next.ThreadId, cancellationToken);
        SetSelection(new(next.ThreadId, next.WorkingDirectory, ""));
        return next;
    }

    public async Task<CodexThreadSummary?> SelectProjectThreadAsync(
        string threadId,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Codex Task 引用不能为空。", nameof(threadId));
        }
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("授权项目目录不能为空。", nameof(projectRoot));
        }

        // The selector card is built from this same project-scoped projection.
        // Do not revalidate a selected Task against the unrelated global/recent
        // list: Codex Desktop can legitimately expose a UI-created project Task
        // only through the exact-root fallback used by GetProjectThreadsAsync.
        IReadOnlyList<CodexThreadSummary> threads = await GetProjectThreadsAsync(
            projectRoot,
            archived: false,
            cancellationToken);
        CodexThreadSummary? selected = threads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (selected is null)
        {
            throw new InvalidOperationException("该 Task 已不在当前可见列表中；未切换。");
        }

        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        if (selected.Archived)
        {
            // Codex Desktop can keep a user-visible project Task in the
            // app-server archived rollout partition. Selecting that visible
            // Task is an explicit request to make it current, so repair the
            // protocol state through the official unarchive method first.
            await client.UnarchiveThreadAsync(selected.ThreadId, cancellationToken);
        }
        // Selecting a Task only changes the Bridge routing target. Do not
        // resume it here: a Task currently open in Codex Desktop can already
        // have an active writer. The message execution path resumes/starts
        // work only when the user actually sends text to this Task.
        SetSelection(new(selected.ThreadId, Path.GetFullPath(projectRoot), ""));
        return selected;
    }

    public async Task<CodexThreadSummary> CreateNamedThreadAsync(
        string workingDirectory,
        string title,
        CancellationToken cancellationToken)
    {
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        CodexThreadStartResult started = await client.StartNamedThreadAsync(
            workingDirectory,
            title,
            cancellationToken);
        SetSelection(new(started.ThreadId, Path.GetFullPath(workingDirectory), ""));
        await Task.Delay(150, cancellationToken);
        IReadOnlyList<CodexThreadSummary> threads = await GetRecentThreadsAsync(cancellationToken);
        return threads.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, started.ThreadId, StringComparison.Ordinal))
            ?? new CodexThreadSummary(
                started.ThreadId,
                title.Trim(),
                string.Empty,
                Path.GetFullPath(workingDirectory),
                string.Empty,
                "idle",
                []);
    }

    internal async Task<CodexThreadSummary> CreateDesktopProjectThreadAsync(
        string projectId, string authorizedRoot, string title, CancellationToken cancellationToken,
        Func<bool>? stillAuthorized = null)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ReadSelection();
            string? expectedScope = _selectionScope;
            CodexDesktopProject project = CodexProjectMenuPolicy.SelectProject(
                await ConnectedClient.ListDesktopProjectsAsync(authorizedRoot, cancellationToken), projectId, null);
            string directory = CodexProjectMenuPolicy.CreationDirectory(project, authorizedRoot);
            ReadSelection();
            if (_selectionScope != expectedScope || stillAuthorized?.Invoke() == false)
                throw new InvalidOperationException("项目授权已变化；未创建 Task。");
            CodexThreadStartResult started = await ConnectedClient.StartNamedThreadAsync(
                directory, title, cancellationToken, project.AppServerId);
            CodexThreadSummary created = CodexProjectMenuPolicy.RequireActionableTask(
                await ConnectedClient.ListDesktopProjectThreadsAsync(project.Id, authorizedRoot, cancellationToken), started.ThreadId);
            SetSelection(new(created.ThreadId, Path.GetFullPath(authorizedRoot), project.Id), expectedScope);
            return created;
        }
        finally { _operationLock.Release(); }
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> GetArchivedThreadsAsync(
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                ? connectedClient
                : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
            return await client.ListThreadSummariesAsync(
                limit: 50,
                useStateDatabaseOnly: true,
                cancellationToken,
                archived: true);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> GetProjectThreadsAsync(
        string projectRoot,
        bool archived,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                ? connectedClient
                : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
            return await client.ListProjectThreadSummariesAsync(
                projectRoot,
                archived,
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CodexThreadSummary> RestoreThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexThreadSummary> archived = await GetArchivedThreadsAsync(cancellationToken);
        CodexThreadSummary target = archived.FirstOrDefault(thread =>
                string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("该已归档 Task 已不可见；未恢复。");
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        await client.UnarchiveThreadAsync(threadId, cancellationToken);
        await client.ResumeThreadAsync(threadId, cancellationToken);
        SetSelection(new(threadId, target.WorkingDirectory, ""));
        return target;
    }

    public async Task<IReadOnlyList<CodexModelSummary>> GetModelsAsync(
        CancellationToken cancellationToken)
    {
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        return await client.ListModelsAsync(cancellationToken);
    }

    public async Task UpdateThreadSettingsAsync(
        string threadId,
        string? model,
        string? effort,
        string? serviceTier,
        CancellationToken cancellationToken)
    {
        CodexThreadSummary? current = await GetCurrentThreadAsync(cancellationToken);
        if (current is null || !string.Equals(current.ThreadId, threadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前 Task 已变化或不可见；设置未修改。");
        }
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        await client.UpdateThreadSettingsAsync(
            threadId,
            model,
            effort,
            serviceTier,
            cancellationToken);
    }

    public async Task<string> ArchiveCurrentThreadAsync(CancellationToken cancellationToken)
    {
        CodexThreadSummary? thread = await GetCurrentThreadAsync(cancellationToken);
        if (thread is null) return "没有可归档的 Codex Task。";
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        await client.ArchiveThreadAsync(thread.ThreadId, cancellationToken);
        if (string.Equals(SelectedThreadId, thread.ThreadId, StringComparison.Ordinal))
        {
            SetSelection(CurrentTaskSelection.Empty);
        }
        await RefreshAsync(cancellationToken);
        return $"已归档当前 Task：{thread.Title}。";
    }

    public async Task<string> CompactCurrentThreadAsync(CancellationToken cancellationToken)
    {
        CodexThreadSummary? thread = await GetCurrentThreadAsync(cancellationToken);
        if (thread is null) return "没有可压缩的 Codex Task。";
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        await client.CompactThreadAsync(thread.ThreadId, cancellationToken);
        return $"已请求压缩当前 Task：{thread.Title}。";
    }

    public async Task<string> CycleCurrentThreadModelAsync(CancellationToken cancellationToken)
    {
        CodexThreadSummary? thread = await GetCurrentThreadAsync(cancellationToken);
        if (thread is null) return "没有可修改模型的 Codex Task。";
        CodexAppServerClient client = _client is { IsConnected: true } connectedClient
            ? connectedClient
            : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
        IReadOnlyList<CodexModelSummary> models = await client.ListModelsAsync(cancellationToken);
        if (models.Count == 0) return "Codex 没有返回可用模型，未改变当前 Task。";
        int currentIndex = models
            .Select((model, index) => (model, index))
            .FirstOrDefault(item => string.Equals(
                item.model.ModelId,
                thread.Model,
                StringComparison.OrdinalIgnoreCase)).index;
        int nextIndex = string.IsNullOrWhiteSpace(thread.Model)
            ? 0
            : (currentIndex + 1) % models.Count;
        CodexModelSummary next = models[nextIndex];
        await client.ResumeThreadAsync(thread.ThreadId, cancellationToken, next.ModelId);
        // Model changes do not change the selected Task.
        return $"已将当前 Task 模型切换为：{next.DisplayName}。";
    }

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
            PublishUsage(CodexUsageSnapshotPolicy.ForNewAppServerSession());
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
                await RefreshUsageCoreAsync(client, force: true, cancellationToken);
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
            await RefreshUsageCoreAsync(_client, force: false, cancellationToken);
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

    public async Task RefreshUsageAsync(bool force, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                ? connectedClient
                : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
            await RefreshUsageCoreAsync(client, force, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            PublishUsageFailure();
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

            PublishUsage(CodexUsageSnapshotPolicy.ForNewAppServerSession());

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

    public async Task<CodexControlledRouteResult> RunPromptInCurrentThreadAsync(
        string prompt,
        CancellationToken cancellationToken,
        Action? onStarted = null,
        string? expectedThreadId = null,
        IReadOnlyList<CodexInputAttachment>? attachments = null,
        Func<bool>? stillAuthorized = null,
        CurrentTaskSelection? queuedSelection = null,
        Action? onSubmitting = null, Action? onNotAccepted = null, Action<string>? onTurnStarted = null)
    {
        string approvedPrompt = prompt?.Trim() ?? throw new ArgumentNullException(nameof(prompt));
        if (approvedPrompt.Length is < 1 or > ReadOnlyPromptMaxLength)
        {
            throw new ArgumentException($"消息长度必须为 1 到 {ReadOnlyPromptMaxLength} 个字符。", nameof(prompt));
        }
        CurrentTaskSelection selection = queuedSelection ?? ReadSelection();
        string threadId = selection.ThreadId;
        if (expectedThreadId is not null && threadId != expectedThreadId)
            throw new InvalidOperationException("当前 Task 已变化，本条指令未发送到其他 Task。");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("尚未选择当前 Task。请先使用“切换 Task”。");
        }

        IDisposable taskLease = await _taskExecutionGate.EnterAsync(threadId, cancellationToken);
        void ReportStarted(string turnId)
        {
            _activeTaskTurns[threadId] = turnId;
            onTurnStarted?.Invoke(turnId);
            onStarted?.Invoke();
        }
        try
        {
            CodexThreadSummary? selected = queuedSelection is null
                ? await GetCurrentThreadAsync(cancellationToken)
                : await GetQueuedThreadAsync(selection, cancellationToken);
            bool SelectionValid() => queuedSelection is not null || ReadSelection() == selection;
            if (selected is null || selected.ThreadId != threadId || !SelectionValid())
                throw new InvalidOperationException("当前 Task 或授权已变化；未发送消息。");
            if (stillAuthorized?.Invoke() == false)
                throw new InvalidOperationException("飞书身份或项目授权已变化；未发送消息。");
            if (queuedSelection is not null && (selected.Status == "active"
                || selected.ActiveFlags.Any(f => f is "waitingOnApproval" or "waitingOnUserInput")
                || new CodexRolloutTaskStateReader().ReadLatestStatus(threadId) is PromLightStatus.Running or PromLightStatus.HumanGate))
                throw new CodexTaskBusyException();
            try
            {
                // Do not hold the app-server operation lock while Desktop runs:
                // status cards and associated lights must remain able to refresh.
                CodexControlledRouteResult? desktop = await new CodexDesktopTurnClient().TryRunAsync(
                    threadId, approvedPrompt, selected.WorkingDirectory,
                    ReportStarted,
                    () => SelectionValid() && stillAuthorized?.Invoke() != false,
                    cancellationToken, attachments: attachments, onSubmitting: onSubmitting, onNotAccepted: onNotAccepted);
                if (desktop is not null) return desktop;
            }
            finally
            {
                _activeTaskTurns.TryRemove(threadId, out _);
            }

            await _operationLock.WaitAsync(cancellationToken);
            bool operationLockHeld = true;
            try
            {
                CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                    ? connectedClient
                    : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
                string selectedProjectRoot = selection.ProjectRoot;
                string desktopProjectId = selection.DesktopProjectId;
                if (string.IsNullOrWhiteSpace(desktopProjectId))
                    desktopProjectId = (await client.ListDesktopProjectsAsync(selectedProjectRoot, cancellationToken))
                        .SingleOrDefault(p => p.AppServerId == selected.EffectiveProjectId)?.Id
                        ?? throw new InvalidOperationException("原 Task 的项目归属无法确认；未发送消息。");
                IReadOnlyList<CodexThreadSummary> visible = await client.ListDesktopProjectThreadsAsync(
                    desktopProjectId, selectedProjectRoot, cancellationToken);
                if (!visible.Any(thread => string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal)
                    && thread.AuthorizedForRouting))
                {
                    if (queuedSelection is null && ReadSelection() == selection) SetSelection(CurrentTaskSelection.Empty);
                    throw new InvalidOperationException("当前 Task 已不可见；请重新选择。");
                }

                if (!SelectionValid())
                    throw new InvalidOperationException("当前 Task 或授权已变化；未发送消息。");
                try
                {
                    await client.ResumeThreadAsync(threadId, cancellationToken);
                }
                catch (CodexAppServerRequestException exception) when (
                    exception.RpcMessage.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "当前 Task 由桌面会话持有，但桌面消息连接暂不可用。请在 Codex Desktop 中打开该 Task 后重试；本条消息尚未执行。",
                        exception);
                }
                _activeTaskTurns.TryRemove(threadId, out _);
                var completion = new TaskCompletionSource<CodexControlledRouteResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                string? startedTurnId = null;

                void ObserveCompletion(CodexAppServerNotification notification)
                {
                    if (notification.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !notification.Parameters.TryGetProperty("threadId", out var threadIdElement)
                        || !string.Equals(threadIdElement.GetString(), threadId, StringComparison.Ordinal)
                        || !notification.Parameters.TryGetProperty("turn", out var turn)
                        || turn.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        return;
                    }
                    string turnIdValue = turn.TryGetProperty("id", out var turnIdElement)
                        ? turnIdElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(startedTurnId)
                        && !string.Equals(turnIdValue, startedTurnId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (string.Equals(notification.Method, "turn/started", StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!string.Equals(notification.Method, "turn/completed", StringComparison.Ordinal)) return;
                    string status = turn.TryGetProperty("status", out var statusElement)
                        ? statusElement.GetString() ?? string.Empty
                        : string.Empty;
                    completion.TrySetResult(new CodexControlledRouteResult(
                        threadId,
                        turnIdValue,
                        status,
                        ReadLastAgentMessage(turn)));
                }

                void ObserveDisconnect(Exception exception) => completion.TrySetException(
                    new IOException("Codex app-server stdio 在 turn/completed 前断开。", exception));

                client.NotificationReceived += ObserveCompletion;
                client.Disconnected += ObserveDisconnect;
                try
                {
                    if (!SelectionValid() || stillAuthorized?.Invoke() == false)
                        throw new InvalidOperationException("当前 Task 或授权已变化；未发送消息。");
                    onSubmitting?.Invoke();
                    CodexTurnStartResult started;
                    try
                    {
                        started = await client.StartTurnInExistingThreadAsync(threadId, approvedPrompt, cancellationToken, attachments);
                    }
                    catch (CodexAppServerRequestException exception) when (queuedSelection is not null
                        && (exception.RpcMessage.Contains("active turn", StringComparison.OrdinalIgnoreCase)
                            || exception.RpcMessage.Contains("already running", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new CodexTaskBusyException();
                    }
                    startedTurnId = started.TurnId;
                    ReportStarted(started.TurnId);
                    // Acceptance of later Feishu messages and status queries must
                    // not wait behind the duration of an app-server-owned turn.
                    _operationLock.Release();
                    operationLockHeld = false;
                    CodexControlledRouteResult result;
                    try
                    {
                        result = await completion.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return new(threadId, started.TurnId, "pending",
                            "桥接等待已结束，但没有发送停止或重复执行指令。请在 Codex Desktop 查看本轮任务；保持当前 Task 关联后，完成结果会继续尝试回传。");
                    }
                    if (!string.Equals(result.TurnId, started.TurnId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Codex 完成通知与已启动的 turn 不一致。");
                    }
                    return result;
                }
                finally
                {
                    client.NotificationReceived -= ObserveCompletion;
                    client.Disconnected -= ObserveDisconnect;
                    _activeTaskTurns.TryRemove(threadId, out _);
                }
            }
            finally
            {
                if (operationLockHeld) _operationLock.Release();
            }
        }
        finally
        {
            taskLease.Dispose();
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

    public async Task<IReadOnlyList<CodexThreadSummary>> GetRecentThreadsAsync(
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            CodexAppServerClient client = _client is { IsConnected: true } connectedClient
                ? connectedClient
                : throw new InvalidOperationException("Codex app-server 会话尚未运行。");
            return await client.ListThreadSummariesAsync(
                limit: 50,
                useStateDatabaseOnly: true,
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RefreshUsageCoreAsync(
        CodexAppServerClient client,
        bool force,
        CancellationToken cancellationToken)
    {
        CodexUsageSnapshot current = UsageSnapshot;
        if (!force
            && current.Availability == CodexUsageAvailability.Fresh
            && current.UpdatedAt is DateTimeOffset updatedAt
            && DateTimeOffset.Now - updatedAt < TimeSpan.FromSeconds(60))
        {
            return;
        }

        PublishUsage(current with { Availability = CodexUsageAvailability.Loading });
        try
        {
            CodexUsageSnapshot refreshed = await client.ReadUsageAsync(cancellationToken);
            if (refreshed.Availability != CodexUsageAvailability.Fresh)
            {
                PublishUsageFailure();
                return;
            }
            PublishUsage(refreshed);
        }
        catch (OperationCanceledException)
        {
            // A cancelled read must not leave the UI permanently in Loading.
            PublishUsageFailure();
            throw;
        }
        catch
        {
            PublishUsageFailure();
        }
    }

    private void PublishUsageFailure()
    {
        CodexUsageSnapshot current = UsageSnapshot;
        PublishUsage(CodexUsageSnapshotPolicy.AfterReadFailure(current));
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

    private void PublishUsage(CodexUsageSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _usageSnapshot = snapshot;
        }
        UsageChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _operationLock.Dispose();
        _controlledRouteLock.Dispose();
    }
}
