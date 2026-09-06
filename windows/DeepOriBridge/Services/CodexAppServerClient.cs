using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedReadOnlyThreads = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _processMessages = new();
    private Process? _process;
    private Task? _receiveTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private bool _disposed;

    public event Action<CodexAppServerNotification>? NotificationReceived;

    public event Action<Exception>? Disconnected;

    public CodexAppServerSessionInfo? SessionInfo { get; private set; }

    public bool IsConnected =>
        !_disposed
        && _process is { HasExited: false };

    internal int? ProcessId => _process is { HasExited: false } process
        ? process.Id
        : null;

    public async Task ConnectAsync(string executable, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("Codex app-server 会话已经启动。");
        }

        _process = StartAppServer(executable);
        _receiveTask = ReceiveLoopAsync(_process.StandardOutput, _lifetime.Token);
        _stderrTask = CaptureStderrAsync(_process.StandardError, _lifetime.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            JsonElement result = await RequestAsync(
                "initialize",
                CreateInitializeParameters(),
                timeout.Token);

            SessionInfo = new CodexAppServerSessionInfo(
                ReadString(result, "platformFamily"),
                ReadString(result, "platformOs"));
            await SendNotificationAsync("initialized", new { }, timeout.Token);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<CodexThreadListResult> ListThreadsAsync(
        int limit,
        bool useStateDatabaseOnly,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexThreadSummary> threads = await ListThreadSummariesAsync(
            limit,
            useStateDatabaseOnly,
            cancellationToken);
        return new CodexThreadListResult(threads.Count);
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadSummariesAsync(
        int limit,
        bool useStateDatabaseOnly,
        CancellationToken cancellationToken,
        bool archived = false)
    {
        EnsureConnected();
        JsonElement result = await RequestAsync(
            "thread/list",
            new
            {
                limit,
                useStateDbOnly = useStateDatabaseOnly,
                archived,
            },
            cancellationToken);
        return ParseThreadSummaries(result)
            .Select(thread => thread with { Archived = archived })
            .ToArray();
    }

    public async Task<IReadOnlyList<CodexDesktopProject>> ListDesktopProjectsAsync(
        string authorizedRoot, CancellationToken cancellationToken)
    {
        EnsureConnected();
        IReadOnlyList<CodexDesktopProject> desktop = CodexDesktopProjectCatalog.Read();
        var canonical = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            JsonElement result = await RequestAsync("project/list", new { limit = 100, cursor }, cancellationToken);
            if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                foreach (JsonElement project in data.EnumerateArray())
                {
                    string id = ReadString(project, "id");
                    if (id.Length > 0) canonical[id] = project.Clone();
                }
            cursor = ReadNullableString(result, "nextCursor");
            if (!string.IsNullOrWhiteSpace(cursor) && !seen.Add(cursor))
                throw new InvalidOperationException("Codex 项目分页异常，请刷新重试。");
        } while (!string.IsNullOrWhiteSpace(cursor));

        return desktop.Where(project =>
            project.Roots.Any(root => AuthorizedProjectScope.Contains(authorizedRoot, root))
            && canonical.TryGetValue(project.AppServerId, out JsonElement server)
            && ServerProjectRootsMatch(server, project.Roots)).ToArray();
    }

    internal static bool ServerProjectRootsMatch(JsonElement server, IReadOnlyList<string> expectedRoots)
    {
        if (!server.TryGetProperty("roots", out JsonElement roots) || roots.ValueKind != JsonValueKind.Array)
            return false;
        string[] actual = roots.EnumerateArray().Select(root => ReadString(root, "path")).ToArray();
        return actual.Length == expectedRoots.Count
            && actual.All(root => expectedRoots.Any(expected => PathsEqual(expected, root)))
            && expectedRoots.All(expected => actual.Any(root => PathsEqual(expected, root)));
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListDesktopProjectThreadsAsync(
        string desktopProjectId, string authorizedRoot, CancellationToken cancellationToken)
    {
        CodexDesktopProject project = (await ListDesktopProjectsAsync(authorizedRoot, cancellationToken))
            .SingleOrDefault(project => project.Id.Equals(desktopProjectId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("项目已移除或不在授权范围内，请刷新项目列表。");
        return await ReadDesktopProjectThreadsAsync(project, authorizedRoot, RequestAsync, cancellationToken,
            id => CodexProjectMembershipReader.Read(id),
            () => CodexDesktopProjectCatalog.Read().SingleOrDefault(p => p.Id == project.Id));
    }

    // Legacy assignments are hints, not proof of current project membership.
    internal static async Task<IReadOnlyList<CodexThreadSummary>> ReadDesktopProjectThreadsAsync(
        CodexDesktopProject project, string authorizedRoot,
        Func<string, object, CancellationToken, Task<JsonElement>> request,
        CancellationToken cancellationToken,
        Func<string, IReadOnlySet<string>>? readCanonicalMembership = null,
        Func<CodexDesktopProject?>? readLatestProject = null)
    {
        var candidates = new Dictionary<string, CodexThreadSummary>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement page = await request("thread/list", new
            {
                projectId = project.AppServerId, archived = false, useStateDbOnly = true, limit = 100, cursor,
                sortKey = "updated_at",
            }, cancellationToken);
            if (!page.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Codex 项目 Task 列表格式无效，请刷新重试。");
            foreach (CodexThreadSummary candidate in ParseThreadSummaries(page))
                if (candidate.ProjectId == project.AppServerId && !candidate.Archived) candidates.TryAdd(candidate.ThreadId, candidate);
            cursor = ReadNullableString(page, "nextCursor");
            if (!string.IsNullOrWhiteSpace(cursor) && !cursors.Add(cursor))
                throw new InvalidOperationException("Codex 项目 Task 分页异常，请刷新重试。");
        } while (!string.IsNullOrWhiteSpace(cursor));

        IReadOnlySet<string>? canonical = readCanonicalMembership?.Invoke(project.AppServerId);
        var legacyIds = new HashSet<string>(StringComparer.Ordinal);
        if (project.LegacyAssignmentsPending && project.ThreadIds.Count > 0)
        {
            // Readability alone is not visibility. Intersect saved IDs with current nonarchived DB rows.
            if (readCanonicalMembership is not null)
                legacyIds.UnionWith(readCanonicalMembership("").Where(project.ThreadIds.Contains));
            else
            {
                cursor = null;
                cursors.Clear();
                do
                {
                    JsonElement page = await request("thread/list", new
                    { archived = false, useStateDbOnly = true, limit = 100, cursor, sortKey = "updated_at" }, cancellationToken);
                    if (!page.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Codex Task 列表格式无效；未使用旧映射。");
                    foreach (CodexThreadSummary t in ParseThreadSummaries(page))
                        if (!t.Archived && string.IsNullOrWhiteSpace(t.ProjectId) && project.ThreadIds.Contains(t.ThreadId)) legacyIds.Add(t.ThreadId);
                    cursor = ReadNullableString(page, "nextCursor");
                    if (!string.IsNullOrWhiteSpace(cursor) && !cursors.Add(cursor)) throw new InvalidOperationException("Codex Task 分页异常。");
                } while (!string.IsNullOrWhiteSpace(cursor));
            }
        }
        IEnumerable<string> ids = (canonical ?? candidates.Keys.ToHashSet(StringComparer.Ordinal)).Union(legacyIds);
        var result = new List<CodexThreadSummary>();
        foreach (string threadId in ids)
        {
            JsonElement read;
            try
            {
                read = await request("thread/read", new { threadId, includeTurns = false }, cancellationToken);
            }
            catch (CodexAppServerRequestException exception) when (
                exception.Code == -32600 && exception.RpcMessage == $"thread not loaded: {threadId}")
            {
                // Deleted/unavailable sidebar references must not hide the rest of the project.
                continue;
            }
            CodexThreadSummary? thread = read.TryGetProperty("thread", out JsonElement value)
                ? ParseThreadSummary(value) : null;
            if (thread is null || thread.ThreadId != threadId || thread.Archived) continue;
            bool legacy = string.IsNullOrWhiteSpace(thread.ProjectId) && legacyIds.Contains(threadId);
            if (!legacy && thread.ProjectId != project.AppServerId) continue;
            result.Add(thread with
            {
                AuthorizedForRouting = AuthorizedProjectScope.Contains(authorizedRoot, thread.WorkingDirectory),
                ProjectDisplayName = project.Name,
                DesktopAssignedProjectId = legacy ? project.AppServerId : "",
            });
        }
        IReadOnlySet<string>? latest = readCanonicalMembership?.Invoke(project.AppServerId);
        IReadOnlySet<string>? unassigned = legacyIds.Count > 0 ? readCanonicalMembership?.Invoke("") : null;
        CodexDesktopProject? latestProject = readLatestProject is null ? project : readLatestProject();
        if (latestProject is null || latestProject.AppServerId != project.AppServerId) return [];
        return result.Where(t => t.DesktopAssignedProjectId.Length == 0
            ? latest is null || latest.Contains(t.ThreadId)
            : latestProject.LegacyAssignmentsPending && latestProject.ThreadIds.Contains(t.ThreadId)
                && (unassigned is null || unassigned.Contains(t.ThreadId))).ToArray();
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListProjectThreadSummariesAsync(
        string projectRoot,
        bool archived,
        CancellationToken cancellationToken,
        int pageSize = 100)
    {
        EnsureConnected();
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        string normalizedRoot = Path.GetFullPath(projectRoot);
        string projectId = await ResolveProjectIdAsync(normalizedRoot, cancellationToken);
        Dictionary<string, CodexThreadSummary> assigned =
            await ListProjectAssignedCandidatesAsync(
                normalizedRoot,
                projectId,
                archived,
                pageSize,
                cancellationToken);
        if (assigned.Count > 0)
        {
            return await HydrateExactRootUserThreadsAsync(
                assigned.Values,
                normalizedRoot,
                expectedProjectId: projectId,
                requireMissingProjectProjection: false,
                cancellationToken);
        }

        // Codex Desktop currently records a UI Task's project assignment in
        // the app layer while a standalone bundled app-server may expose that
        // same Task with projectId=null. Fall back only after the canonical
        // project query returned zero, and only to an objectively user-created
        // Task whose cwd exactly equals the already-validated single root.
        // A UI-visible Task can also be moved by app-server into its archived
        // rollout set while remaining visible in the desktop project. Query
        // both protocol partitions for the normal selector, exhaust every
        // cursor, and deduplicate rollout repairs by canonical thread ID.
        var unassigned = new Dictionary<string, CodexThreadSummary>(StringComparer.Ordinal);
        bool[] archivePartitions = archived ? [true] : [false, true];
        foreach (bool archivePartition in archivePartitions)
        {
            await AddExactCwdCandidatesAsync(
                unassigned,
                normalizedRoot,
                archivePartition,
                pageSize,
                cancellationToken);
        }
        return await HydrateExactRootUserThreadsAsync(
            unassigned.Values,
            normalizedRoot,
            expectedProjectId: null,
            requireMissingProjectProjection: true,
            cancellationToken);
    }

    private async Task<Dictionary<string, CodexThreadSummary>>
        ListProjectAssignedCandidatesAsync(
            string projectRoot,
            string projectId,
            bool archived,
            int pageSize,
            CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, CodexThreadSummary>(StringComparer.Ordinal);
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            JsonElement result = await RequestAsync(
                "thread/list",
                new
                {
                    limit = pageSize,
                    useStateDbOnly = false,
                    archived,
                    projectId,
                    cursor,
                },
                cancellationToken);
            foreach (CodexThreadSummary candidate in ParseThreadSummaries(result))
            {
                if (PathsEqual(projectRoot, candidate.WorkingDirectory))
                {
                    candidates.TryAdd(
                        candidate.ThreadId,
                        candidate with { Archived = archived });
                }
            }
            cursor = ReadNullableString(result, "nextCursor");
            if (string.IsNullOrWhiteSpace(cursor)) break;
            if (!seenCursors.Add(cursor))
            {
                throw new InvalidOperationException(
                    "Codex thread/list 返回了重复 cursor；不会接受不完整的项目 Task 集合。");
            }
        }
        return candidates;
    }

    private async Task AddExactCwdCandidatesAsync(
        Dictionary<string, CodexThreadSummary> candidates,
        string projectRoot,
        bool archived,
        int pageSize,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            JsonElement result = await RequestAsync(
                "thread/list",
                new
                {
                    limit = pageSize,
                    useStateDbOnly = false,
                    archived,
                    cwd = projectRoot,
                    cursor,
                },
                cancellationToken);
            foreach (CodexThreadSummary candidate in ParseThreadSummaries(result))
            {
                if (PathsEqual(projectRoot, candidate.WorkingDirectory)
                    && string.IsNullOrWhiteSpace(candidate.ProjectId))
                {
                    candidates.TryAdd(
                        candidate.ThreadId,
                        candidate with { Archived = archived });
                }
            }
            cursor = ReadNullableString(result, "nextCursor");
            if (string.IsNullOrWhiteSpace(cursor)) break;
            if (!seenCursors.Add(cursor))
            {
                throw new InvalidOperationException(
                    "Codex thread/list 返回了重复 cursor；不会接受不完整的项目 Task 集合。");
            }
        }
    }

    private async Task<IReadOnlyList<CodexThreadSummary>>
        HydrateExactRootUserThreadsAsync(
            IEnumerable<CodexThreadSummary> candidates,
            string projectRoot,
            string? expectedProjectId,
            bool requireMissingProjectProjection,
            CancellationToken cancellationToken)
    {
        var accepted = new List<CodexThreadSummary>();
        foreach (CodexThreadSummary candidate in candidates)
        {
            // thread/list currently omits threadSource for unassigned records.
            // Hydrate metadata without turns so provenance is checked
            // objectively instead of inferred from a title.
            JsonElement readResult = await RequestAsync(
                "thread/read",
                new { threadId = candidate.ThreadId, includeTurns = false },
                cancellationToken);
            CodexThreadSummary? hydrated =
                readResult.TryGetProperty("thread", out JsonElement thread)
                    ? ParseThreadSummary(thread)
                    : null;
            if (hydrated is null
                || !hydrated.ThreadId.Equals(candidate.ThreadId, StringComparison.Ordinal)
                || !IsExactRootUserThread(
                    hydrated,
                    projectRoot,
                    requireMissingProjectProjection))
            {
                continue;
            }
            if (!requireMissingProjectProjection
                && !hydrated.ProjectId.Equals(expectedProjectId, StringComparison.Ordinal))
            {
                continue;
            }
            accepted.Add(hydrated with { Archived = candidate.Archived });
        }
        return accepted;
    }

    internal static bool IsExactRootUserThread(
        CodexThreadSummary thread,
        string projectRoot,
        bool requireMissingProjectProjection) =>
        PathsEqual(projectRoot, thread.WorkingDirectory)
        && thread.ThreadSource.Equals("user", StringComparison.Ordinal)
        && (!requireMissingProjectProjection
            || string.IsNullOrWhiteSpace(thread.ProjectId));

    private async Task<string> ResolveProjectIdAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var matches = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            JsonElement result = await RequestAsync(
                "project/list",
                new { limit = 100, cursor },
                cancellationToken);
            if (result.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement project in data.EnumerateArray())
                {
                    string id = ReadString(project, "id");
                    if (string.IsNullOrWhiteSpace(id)
                        || !project.TryGetProperty("roots", out JsonElement roots)
                        || roots.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    if (HasExactAuthorizedRootSet(project, projectRoot))
                    {
                        matches.Add(id);
                    }
                }
            }
            cursor = ReadNullableString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor) && seenCursors.Add(cursor));

        return matches.Count switch
        {
            1 => matches.Single(),
            0 => throw new InvalidOperationException(
                "授权目录不是 Codex Desktop 中已保存的项目；不会用相似路径或 Git 仓库猜测 Task。"),
            _ => throw new InvalidOperationException(
                "授权目录对应多个 Codex 项目；无法唯一确定 Task 数据源。"),
        };
    }

    private static IReadOnlyList<CodexThreadSummary> ParseThreadSummaries(JsonElement result)
    {
        if (!result.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CodexThreadSummary>();
        }

        var threads = new List<CodexThreadSummary>();
        foreach (JsonElement thread in data.EnumerateArray())
        {
            CodexThreadSummary? summary = ParseThreadSummary(thread);
            if (summary is not null)
            {
                threads.Add(summary);
            }
        }
        return threads;
    }

    private static CodexThreadSummary? ParseThreadSummary(JsonElement thread)
    {
        string id = ReadString(thread, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        string status = thread.TryGetProperty("status", out JsonElement statusValue)
            && statusValue.ValueKind == JsonValueKind.Object
            ? ReadString(statusValue, "type")
            : string.Empty;
        string[] activeFlags = statusValue.ValueKind == JsonValueKind.Object
            && statusValue.TryGetProperty("activeFlags", out JsonElement flagsValue)
            && flagsValue.ValueKind == JsonValueKind.Array
                ? flagsValue.EnumerateArray()
                    .Where(flag => flag.ValueKind == JsonValueKind.String)
                    .Select(flag => flag.GetString() ?? string.Empty)
                    .Where(flag => flag.Length > 0)
                    .ToArray()
                : [];
        string preview = SafeDisplay(ReadString(thread, "preview"), "无预览");
        return new CodexThreadSummary(
            id,
            ThreadDisplayTitle(thread),
            preview,
            ReadString(thread, "cwd"),
            ReadString(thread, "model"),
            status,
            activeFlags,
            ReadString(thread, "reasoningEffort"),
            SafeDisplay(ReadString(thread, "serviceTier"), "default"),
            ReadString(thread, "projectId"),
            ReadString(thread, "threadSource"),
            Archived: thread.TryGetProperty("archived", out JsonElement archived) && archived.ValueKind == JsonValueKind.True);
    }

    private static string? ReadNullableString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasExactAuthorizedRootSet(JsonElement project, string projectRoot)
    {
        if (!project.TryGetProperty("roots", out JsonElement roots)
            || roots.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement.ArrayEnumerator enumerator = roots.EnumerateArray();
        if (!enumerator.MoveNext()
            || !PathsEqual(projectRoot, ReadString(enumerator.Current, "path")))
        {
            return false;
        }

        // A broader multi-root project can contain the authorized directory,
        // but querying it by projectId would also expose Tasks from other roots.
        return !enumerator.MoveNext();
    }

    internal static object CreateInitializeParameters() => new
    {
        clientInfo = new
        {
            name = "deepori-bridge-windows",
            title = "DeepOri Bridge for Windows",
            version = "0.1.0",
        },
        // project/list is an experimental app-server API. The capability must
        // be negotiated during initialize before any project-scoped Task query.
        capabilities = new { experimentalApi = true },
    };

    public async Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        await RequestAsync("thread/archive", new { threadId }, cancellationToken);
    }

    public async Task CompactThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        await RequestAsync("thread/compact/start", new { threadId }, cancellationToken);
    }

    public async Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        await RequestAsync("thread/unarchive", new { threadId }, cancellationToken);
    }

    public async Task SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        string title = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length is < 1 or > 120)
        {
            throw new ArgumentException("Task 标题必须为 1 到 120 个字符。", nameof(name));
        }
        await RequestAsync("thread/name/set", new { threadId, name = title }, cancellationToken);
    }

    public async Task UpdateThreadSettingsAsync(
        string threadId,
        string? model,
        string? effort,
        string? serviceTier,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        await RequestAsync(
            "thread/settings/update",
            new { threadId, model, effort, serviceTier },
            cancellationToken);
    }

    public async Task ResumeThreadAsync(
        string threadId,
        CancellationToken cancellationToken,
        string? model = null)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        await RequestAsync(
            "thread/resume",
            new { threadId, excludeTurns = true, model },
            cancellationToken);
    }

    public async Task<IReadOnlyList<CodexModelSummary>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        JsonElement result = await RequestAsync(
            "model/list",
            new { includeHidden = false },
            cancellationToken);
        if (!result.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CodexModelSummary>();
        }

        var models = new List<CodexModelSummary>();
        foreach (JsonElement model in data.EnumerateArray())
        {
            string modelId = SafeDisplay(ReadString(model, "model"), ReadString(model, "id"));
            if (string.IsNullOrWhiteSpace(modelId)) continue;
            string[] efforts = model.TryGetProperty("supportedReasoningEfforts", out JsonElement effortValues)
                && effortValues.ValueKind == JsonValueKind.Array
                    ? effortValues.EnumerateArray()
                        .Select(item => ReadString(item, "reasoningEffort"))
                        .Where(value => value.Length > 0)
                        .ToArray()
                    : [];
            CodexServiceTierSummary[] tiers = model.TryGetProperty("serviceTiers", out JsonElement tierValues)
                && tierValues.ValueKind == JsonValueKind.Array
                    ? tierValues.EnumerateArray()
                        .Select(item => new CodexServiceTierSummary(
                            ReadString(item, "id"),
                            ReadString(item, "name"),
                            ReadString(item, "description")))
                        .Where(item => item.Id.Length > 0)
                        .ToArray()
                    : [];
            models.Add(new CodexModelSummary(
                modelId,
                SafeDisplay(ReadString(model, "displayName"), modelId),
                efforts,
                tiers));
        }
        return models;
    }

    public async Task<CodexThreadStartResult> StartNamedThreadAsync(
        string workingDirectory,
        string name,
        CancellationToken cancellationToken,
        string? projectId = null)
    {
        EnsureConnected();
        string cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException("已授权的 Codex 项目目录不存在。");
        }
        JsonElement result = await RequestAsync(
            "thread/start",
            CreateNamedThreadParameters(cwd, projectId),
            cancellationToken);
        string threadId = ReadNestedString(result, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codex thread/start 响应缺少 thread.id。");
        }
        await SetThreadNameAsync(threadId, name, cancellationToken);
        return new CodexThreadStartResult(threadId, "workspaceWrite", "on-request");
    }

    internal static object CreateNamedThreadParameters(string cwd, string? projectId) => new
    {
        cwd, projectId, approvalPolicy = "on-request", sandbox = "workspace-write",
    };

    public async Task<CodexUsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        JsonElement result = await RequestAsync(
            "account/rateLimits/read",
            new { },
            cancellationToken);
        return ParseUsageSnapshot(result, DateTimeOffset.Now);
    }

    internal static CodexUsageSnapshot ParseUsageSnapshot(
        JsonElement result,
        DateTimeOffset observedAt)
    {
        string planType = DisplayPlanType(ReadString(result, "planType"));
        var buckets = new List<CodexUsageBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out JsonElement byLimit)
            && byLimit.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in byLimit.EnumerateObject())
            {
                CodexUsageBucket? bucket = ParseUsageBucket(
                    property.Name, property.Value, planType);
                if (bucket is not null)
                {
                    buckets.Add(bucket);
                }
            }
        }
        if (buckets.Count == 0
            && result.TryGetProperty("rateLimits", out JsonElement fallback)
            && fallback.ValueKind == JsonValueKind.Object)
        {
            CodexUsageBucket? bucket = ParseUsageBucket("default", fallback, planType);
            if (bucket is not null)
            {
                buckets.Add(bucket);
            }
        }

        buckets.Sort((left, right) =>
        {
            bool leftCodex = left.LimitId.Equals("codex", StringComparison.Ordinal);
            bool rightCodex = right.LimitId.Equals("codex", StringComparison.Ordinal);
            if (leftCodex != rightCodex) return leftCodex ? -1 : 1;
            return StringComparer.Ordinal.Compare(left.LimitId, right.LimitId);
        });

        return buckets.Count == 0
            ? CodexUsageSnapshot.Initial
            : new CodexUsageSnapshot(CodexUsageAvailability.Fresh, buckets, observedAt);
    }

    private static CodexUsageBucket? ParseUsageBucket(
        string limitId,
        JsonElement source,
        string inheritedPlanType)
    {
        JsonElement windows = source.TryGetProperty("rateLimits", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : source;
        var parsed = new List<CodexUsageWindow>();
        foreach (string slot in new[] { "primary", "secondary" })
        {
            if (!windows.TryGetProperty(slot, out JsonElement window)
                || window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadFiniteNumber(window, "usedPercent", out double usedValue))
            {
                continue;
            }
            double used = Math.Clamp(usedValue, 0, 100);
            int duration = Math.Max(0, ReadInt(window, "windowDurationMins"));
            DateTimeOffset? resetAt = ReadUnixTimestamp(window, "resetsAt");
            parsed.Add(new CodexUsageWindow(
                UsageWindowLabel(duration), used, 100 - used, duration, resetAt));
        }

        if (parsed.Count == 0)
        {
            return null;
        }

        return new CodexUsageBucket(
            limitId,
            SafeDisplay(ReadString(source, "limitName"), SafeDisplay(limitId, "未知额度")),
            string.IsNullOrWhiteSpace(ReadString(source, "planType"))
                ? inheritedPlanType
                : DisplayPlanType(ReadString(source, "planType")),
            parsed);
    }

    internal static string UsageWindowLabel(int minutes) => minutes switch
    {
        300 => "5小时",
        10080 => "每周",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}小时",
        > 0 => $"{minutes}分钟",
        _ => "未知窗口",
    };

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long seconds)
            || seconds <= 0)
        {
            return null;
        }
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int ReadInt(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) ? number : 0;

    private static bool TryReadFiniteNumber(
        JsonElement source,
        string name,
        out double number)
    {
        number = 0;
        return source.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number)
            && double.IsFinite(number);
    }

    private static string SafeDisplay(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    internal static string ThreadDisplayTitle(JsonElement thread)
    {
        string name = ReadString(thread, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        string preview = ReadString(thread, "preview");
        string normalizedPreview = string.Join(
            ' ',
            preview.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return SafeDisplay(normalizedPreview, "未命名 Task");
    }

    private static string DisplayPlanType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "" => "未知套餐",
        _ => "其他套餐",
    };

    public async Task<CodexThreadStartResult> StartThreadAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        string cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"Codex 工作目录不存在：{cwd}");
        }

        JsonElement result = await RequestAsync(
            "thread/start",
            new
            {
                cwd,
                approvalPolicy = "never",
                sandbox = "read-only",
            },
            cancellationToken);

        CodexThreadStartResult verified = ValidateThreadStartResponse(result, cwd);
        _verifiedReadOnlyThreads.TryAdd(verified.ThreadId, 0);
        return verified;
    }

    internal static CodexThreadStartResult ValidateThreadStartResponse(
        JsonElement result,
        string expectedWorkingDirectory)
    {
        string threadId = ReadNestedString(result, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codex thread/start 响应缺少 thread.id。");
        }

        string approvalPolicy = ReadString(result, "approvalPolicy");
        if (!string.Equals(approvalPolicy, "never", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codex thread/start 生效的 approvalPolicy 不是 never（实际：{SafePolicyLabel(approvalPolicy)}）；已阻止 turn/start。");
        }

        string sandboxType = result.TryGetProperty("sandbox", out JsonElement sandbox)
            && sandbox.ValueKind == JsonValueKind.Object
                ? ReadString(sandbox, "type")
                : string.Empty;
        if (!string.Equals(sandboxType, "readOnly", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codex thread/start 生效的 sandbox 不是 readOnly（实际：{SafePolicyLabel(sandboxType)}）；已阻止 turn/start。");
        }

        string actualCwd = ReadString(result, "cwd");
        string expectedCwd = Path.GetFullPath(expectedWorkingDirectory);
        if (string.IsNullOrWhiteSpace(actualCwd)
            || !string.Equals(
                Path.GetFullPath(actualCwd),
                expectedCwd,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Codex thread/start 生效的工作目录与本机授权目录不一致；已阻止 turn/start。");
        }

        return new CodexThreadStartResult(threadId, sandboxType, approvalPolicy);
    }

    public async Task<CodexTurnStartResult> StartTurnAsync(
        string threadId,
        string prompt,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Codex threadId 和测试提示词不能为空。");
        }

        if (!_verifiedReadOnlyThreads.ContainsKey(threadId))
        {
            throw new InvalidOperationException(
                "Codex thread 未通过服务端 readOnly/never 生效策略核验；已阻止 turn/start。");
        }

        JsonElement result = await RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[]
                {
                    new { type = "text", text = prompt },
                },
            },
            cancellationToken);
        string turnId = ReadNestedString(result, "turn", "id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("Codex turn/start 响应缺少 turn.id。");
        }

        string status = ReadNestedString(result, "turn", "status");
        return new CodexTurnStartResult(threadId, turnId, status);
    }

    public async Task<CodexTurnStartResult> StartTurnInExistingThreadAsync(
        string threadId,
        string prompt,
        CancellationToken cancellationToken,
        IReadOnlyList<CodexInputAttachment>? attachments = null)
    {
        EnsureConnected();
        EnsureThreadId(threadId);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("消息内容不能为空。", nameof(prompt));
        }
        JsonElement result = await RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = FeishuAttachments.BuildTurnInput(prompt, attachments),
            },
            cancellationToken);
        string turnId = ReadNestedString(result, "turn", "id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("Codex turn/start 响应缺少 turn.id。");
        }
        return new CodexTurnStartResult(
            threadId,
            turnId,
            ReadNestedString(result, "turn", "status"));
    }

    private static void EnsureThreadId(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Codex threadId 不能为空。", nameof(threadId));
        }
    }

    public async Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            throw new ArgumentException("Codex threadId 和 turnId 不能为空。");
        }

        JsonElement result = await RequestAsync(
            "turn/interrupt",
            new
            {
                threadId,
                turnId,
            },
            cancellationToken);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex turn/interrupt 响应不是对象。");
        }
    }

    internal static bool IsTerminalTurnStatus(string status) =>
        string.Equals(status, "completed", StringComparison.Ordinal)
        || string.Equals(status, "interrupted", StringComparison.Ordinal)
        || string.Equals(status, "failed", StringComparison.Ordinal);

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("无法登记 Codex app-server 请求。");
        }

        try
        {
            await SendMessageAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters,
                }),
                cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        SendMessageAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }),
            cancellationToken);

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        Process process = _process is { HasExited: false } running
            ? running
            : throw new InvalidOperationException("Codex app-server stdio 会话未运行。");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw BuildProcessExitException();
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IReadOnlyList<JsonElement> messages;
                try
                {
                    messages = ParseStdioMessages(line);
                }
                catch (JsonException exception)
                {
                    int adjacentObjectBoundaries = line.Count(
                        character => character == '}');
                    throw new InvalidOperationException(
                        $"Codex app-server stdio framing 无效（长度 {line.Length}，对象结束符 {adjacentObjectBoundaries}，JSON 偏移 {exception.BytePositionInLine}，首尾字符 {(int)line[0]}/{(int)line[^1]}）；原始内容未记录。",
                        exception);
                }

                foreach (JsonElement message in messages)
                {
                    await DispatchMessageAsync(message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            Exception error = terminalError
                ?? new InvalidOperationException("Codex app-server stdio 会话已结束。");
            foreach (TaskCompletionSource<JsonElement> completion in _pending.Values)
            {
                completion.TrySetException(error);
            }

            if (terminalError is not null)
            {
                Disconnected?.Invoke(terminalError);
            }
        }
    }

    internal static IReadOnlyList<JsonElement> ParseStdioMessages(string payload)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(payload);
        var messages = new List<JsonElement>();
        int offset = 0;
        while (offset < utf8.Length)
        {
            while (offset < utf8.Length && char.IsWhiteSpace((char)utf8[offset]))
            {
                offset++;
            }

            if (offset >= utf8.Length)
            {
                break;
            }

            var reader = new Utf8JsonReader(
                utf8.AsSpan(offset),
                isFinalBlock: true,
                state: default);
            if (!reader.Read())
            {
                throw new JsonException("Codex app-server stdio payload 不包含完整 JSON 消息。");
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            messages.Add(document.RootElement.Clone());
            offset += checked((int)reader.BytesConsumed);
        }

        if (messages.Count == 0)
        {
            throw new JsonException("Codex app-server stdio payload 不包含 JSON 消息。");
        }

        return messages;
    }

    private async Task DispatchMessageAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (root.TryGetProperty("id", out JsonElement idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out long responseId)
            && _pending.TryGetValue(responseId, out TaskCompletionSource<JsonElement>? completion)
            && !root.TryGetProperty("method", out _))
        {
            if (root.TryGetProperty("error", out JsonElement error))
            {
                completion.TrySetException(
                    new CodexAppServerRequestException(
                        error.TryGetProperty("code", out JsonElement code) && code.TryGetInt32(out int number) ? number : 0,
                        ReadString(error, "message")));
            }
            else if (root.TryGetProperty("result", out JsonElement result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(
                    new InvalidOperationException("Codex app-server 响应缺少 result。"));
            }

            return;
        }

        if (!root.TryGetProperty("method", out JsonElement methodElement))
        {
            return;
        }

        string method = methodElement.GetString() ?? string.Empty;
        if (root.TryGetProperty("id", out JsonElement requestId))
        {
            await RejectServerRequestAsync(requestId.Clone(), method, cancellationToken);
            return;
        }

        JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
            ? paramsElement.Clone()
            : default;
        NotificationReceived?.Invoke(
            new CodexAppServerNotification(method, parameters, DateTimeOffset.Now));
    }

    private Task RejectServerRequestAsync(
        JsonElement requestId,
        string method,
        CancellationToken cancellationToken) =>
        SendMessageAsync(
            BuildRejectedServerRequestResponse(requestId, method),
            cancellationToken);

    internal static string BuildRejectedServerRequestResponse(
        JsonElement requestId,
        string method)
    {
        object? result = method switch
        {
            "item/commandExecution/requestApproval" => new { decision = "cancel" },
            "item/fileChange/requestApproval" => new { decision = "cancel" },
            "execCommandApproval" => new { decision = "abort" },
            "applyPatchApproval" => new { decision = "abort" },
            "item/permissions/requestApproval" => new
            {
                permissions = new
                {
                    fileSystem = (object?)null,
                    network = (object?)null,
                },
                scope = (object?)null,
            },
            _ => null,
        };

        return result is not null
            ? JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                result,
            })
            : JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                error = new
                {
                    code = -32001,
                    message = "DeepOri Bridge rejects server-initiated input and tool requests.",
                },
            });
    }

    private async Task CaptureStderrAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                AppendProcessMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Process StartAppServer(string executable)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = utf8,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Codex app-server stdio 子进程。");
    }

    private void AppendProcessMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_processMessages)
        {
            if (_processMessages.Length < 4096)
            {
                _processMessages.AppendLine(message);
            }
        }
    }

    private Exception BuildProcessExitException()
    {
        string detail;
        lock (_processMessages)
        {
            detail = _processMessages.ToString().Trim();
        }

        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? "Codex app-server stdio 已关闭。"
                : $"Codex app-server stdio 已关闭：{detail}");
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Codex app-server stdio 尚未连接。");
        }
    }

    private static string SafePolicyLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "缺失" : value;

    private static string ReadString(JsonElement element, string propertyName, string fallback = "") =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : fallback;

    private static string ReadNestedString(
        JsonElement element,
        string containerName,
        string propertyName) =>
        element.TryGetProperty(containerName, out JsonElement container)
        && container.ValueKind == JsonValueKind.Object
        && container.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_process is { HasExited: false } running)
        {
            running.StandardInput.Close();
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
            }
        }

        if (_process is { HasExited: false } process)
        {
            using var processTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(processTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        if (_stderrTask is not null)
        {
            try
            {
                await _stderrTask;
            }
            catch
            {
            }
        }

        _verifiedReadOnlyThreads.Clear();
        _process?.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
    }
}
