using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record PromLightTransientSignal(PromLightStatus Status, DateTimeOffset? ExpiresAt)
{
    internal static PromLightTransientSignal? Create(PromLightStatus status, DateTimeOffset now) =>
        status == PromLightStatus.Idle ? null : new(status,
            status == PromLightStatus.Error ? now.AddSeconds(8) : null);

    internal static PromLightStatus Effective(
        PromLightStatus taskStatus, bool hasAssociations,
        PromLightTransientSignal? signal, DateTimeOffset now) =>
        // A bound lamp represents its selected Tasks, not unrelated transport errors.
        hasAssociations || signal is null || signal.ExpiresAt <= now
            ? taskStatus
            : PromLightTaskStatusPolicy.Aggregate([taskStatus, signal.Status]);
}

internal static class PromLightTaskStatusPolicy
{
    internal static PromLightStatus Aggregate(IEnumerable<PromLightStatus> statuses) =>
        statuses.DefaultIfEmpty(PromLightStatus.Idle).MaxBy(Priority);

    internal static PromLightStatus Resolve(
        CodexThreadSummary thread,
        PromLightStatus rolloutStatus,
        bool waitingOnApproval)
    {
        if (waitingOnApproval
            || thread.ActiveFlags.Any(flag =>
                flag.Equals("waitingOnApproval", StringComparison.Ordinal)
                || flag.Equals("waitingOnUserInput", StringComparison.Ordinal)))
        {
            return PromLightStatus.HumanGate;
        }

        return thread.Status switch
        {
            "systemError" => PromLightStatus.Error,
            "active" => PromLightStatus.Running,
            _ => rolloutStatus,
        };
    }

    private static int Priority(PromLightStatus status) => status switch
    {
        PromLightStatus.Error => 4,
        PromLightStatus.HumanGate => 3,
        PromLightStatus.Running => 2,
        _ => 1,
    };
}

internal sealed class CodexRolloutTaskStateReader
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxLineBytes = 8 * 1024 * 1024;
    private readonly string _sessionsRoot;

    internal CodexRolloutTaskStateReader(string? sessionsRoot = null) =>
        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    private readonly ConcurrentDictionary<string, string> _paths =
        new(StringComparer.Ordinal);

    internal PromLightStatus ReadLatestStatus(string taskId)
    {
        string? path = ResolvePath(taskId);
        if (path is null)
        {
            return PromLightStatus.Idle;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var terminals = new Dictionary<string, PromLightStatus>(StringComparer.Ordinal);
            foreach (byte[] line in ReverseCompleteLines(stream))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("payload", out JsonElement payload)
                        || payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string type = ReadString(payload, "type");
                    string turnId = ReadString(payload, "turn_id");
                    if (turnId.Length == 0)
                    {
                        continue;
                    }

                    if (type == "task_complete")
                    {
                        terminals.TryAdd(turnId, PromLightStatus.Idle);
                    }
                    else if (type == "turn_aborted")
                    {
                        terminals.TryAdd(turnId, PromLightStatus.Idle);
                    }
                    else if (type == "task_failed")
                    {
                        terminals.TryAdd(turnId, PromLightStatus.Error);
                    }
                    else if (type == "task_started")
                    {
                        return terminals.TryGetValue(turnId, out PromLightStatus terminal)
                            ? terminal
                            : PromLightStatus.Running;
                    }
                }
                catch (JsonException)
                {
                    // An unrelated malformed historical record cannot authorize a light state.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            _paths.TryRemove(taskId, out _);
        }

        return PromLightStatus.Idle;
    }

    internal string ReadLatestReply(string taskId)
        => ReadLatestCompletedResult(taskId)?.AgentMessageText ?? "";

    internal CodexDesktopSyncSnapshot ReadDesktopSyncSnapshot(string taskId)
    {
        string? path = ResolvePath(taskId);
        if (path is null) return CodexDesktopSyncSnapshot.None;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var terminals = new Dictionary<string, CodexDesktopSyncSnapshot>(StringComparer.Ordinal);
            CodexDesktopSyncSnapshot? latestTerminal = null;
            foreach (byte[] line in ReverseCompleteLines(stream))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (ReadString(root, "type") != "event_msg"
                        || !root.TryGetProperty("payload", out JsonElement payload)
                        || payload.ValueKind != JsonValueKind.Object) continue;
                    string type = ReadString(payload, "type");
                    string turnId = ReadString(payload, "turn_id");
                    if (turnId.Length == 0) continue;
                    if (type is "task_complete" or "task_failed" or "turn_aborted")
                    {
                        var terminal = new CodexDesktopSyncSnapshot(
                            type == "task_complete" ? "completed"
                                : type == "task_failed" ? "failed" : "interrupted",
                            turnId,
                            ReadString(payload, "last_agent_message"));
                        terminals.TryAdd(turnId, terminal);
                        latestTerminal ??= terminal;
                    }
                    else if (type == "task_started")
                    {
                        return terminals.TryGetValue(turnId, out CodexDesktopSyncSnapshot? terminal)
                            ? terminal
                            : new CodexDesktopSyncSnapshot("running", turnId, "");
                    }
                }
                catch (JsonException) { }
            }
            return latestTerminal ?? CodexDesktopSyncSnapshot.None;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _paths.TryRemove(taskId, out _);
            return CodexDesktopSyncSnapshot.None;
        }
    }

    internal string ReadGeneratedImageLinks(string taskId, string turnId)
    {
        string? path = ResolvePath(taskId, includeArchived: true);
        if (path is null || string.IsNullOrWhiteSpace(turnId)) return "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool collecting = false;
            var images = new List<string>();
            foreach (byte[] line in ReverseCompleteLines(stream))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (ReadString(root, "type") != "event_msg"
                        || !root.TryGetProperty("payload", out JsonElement payload)
                        || payload.ValueKind != JsonValueKind.Object) continue;
                    string type = ReadString(payload, "type");
                    if (!collecting)
                    {
                        collecting = type == "task_complete" && ReadString(payload, "turn_id") == turnId;
                        continue;
                    }
                    if (type == "task_started")
                        return ReadString(payload, "turn_id") == turnId
                            ? string.Join("\n", images.AsEnumerable().Reverse().Select(image => $"![生成图片](<{image}>)")) : "";
                    // Older/overlapping terminal boundaries cannot contribute images.
                    if (type is "task_complete" or "task_failed" or "turn_aborted") return "";
                    string saved = ReadString(payload, "saved_path");
                    if (type == "image_generation_end" && images.Count < 8 && Path.IsPathFullyQualified(saved)
                        && saved.IndexOfAny(['<', '>', '\r', '\n']) < 0 && !images.Contains(saved)) images.Add(saved);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return "";
    }

    internal CodexControlledRouteResult? ReadLatestCompletedResult(string taskId)
        => ReadTerminalResult(taskId, null);

    internal IReadOnlyList<CodexControlledRouteResult> ReadTerminalResultsAfter(string taskId, string? afterTurnId)
    {
        string? path = ResolvePath(taskId, includeArchived: true);
        if (path is null) return [];
        var results = new List<CodexControlledRouteResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (byte[] line in ReverseCompleteLines(stream))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (ReadString(root, "type") != "event_msg" || !root.TryGetProperty("payload", out JsonElement payload)) continue;
                string type = ReadString(payload, "type"), turn = ReadString(payload, "turn_id");
                if (type is not ("task_complete" or "task_failed" or "turn_aborted") || turn.Length == 0) continue;
                if (turn == afterTurnId) break;
                if (seen.Add(turn)) results.Add(new(taskId, turn, type == "task_complete" ? "completed"
                    : type == "task_failed" ? "failed" : "interrupted", ReadString(payload, "last_agent_message")));
                if (afterTurnId is null) break; // First selection: establish a baseline, do not replay history.
            }
            catch (JsonException) { }
        }
        results.Reverse();
        return results;
    }

    internal CodexControlledRouteResult? ReadTerminalResult(string taskId, string? expectedTurnId)
    {
        // Archiving changes storage, not the terminal status of an accepted message.
        // Only terminal recovery searches archives; they never become selectable/runnable Tasks.
        string? path = ResolvePath(taskId, includeArchived: true);
        if (path is null) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (byte[] line in ReverseCompleteLines(stream))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (ReadString(root, "type") != "event_msg"
                        || !root.TryGetProperty("payload", out JsonElement payload)
                        || payload.ValueKind != JsonValueKind.Object) continue;
                    string type = ReadString(payload, "type");
                    string turnId = ReadString(payload, "turn_id");
                    if ((expectedTurnId is null && type == "task_complete")
                        || (expectedTurnId == turnId && type is "task_complete" or "task_failed" or "turn_aborted"))
                        return new(taskId, turnId, type == "task_complete" ? "completed"
                            : type == "turn_aborted" ? "interrupted" : "failed", ReadString(payload, "last_agent_message"));
                }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal string? ResolvePath(string taskId, bool includeArchived = false)
    {
        if (taskId.Length is < 1 or > 128
            || taskId.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            return null;
        }

        string suffix = $"-{taskId}.jsonl";
        string resumedPrefix = $"-{taskId}_";
        try
        {
            string archivedRoot = Path.Combine(Path.GetDirectoryName(_sessionsRoot)!, "archived_sessions");
            IEnumerable<string> roots = includeArchived ? new[] { _sessionsRoot, archivedRoot } : new[] { _sessionsRoot };
            string? match = roots.Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, $"*-{taskId}*.jsonl", SearchOption.AllDirectories))
                .Where(candidate =>
                {
                    string name = Path.GetFileName(candidate);
                    if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
                    int index = name.IndexOf(resumedPrefix, StringComparison.Ordinal);
                    if (index < 0 || !Guid.TryParse(name[(index + resumedPrefix.Length)..^6], out _)) return false;
                    using var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var input = new StreamReader(file);
                    try
                    {
                        using JsonDocument metadata = JsonDocument.Parse(input.ReadLine() ?? "{}");
                        return ReadString(metadata.RootElement, "type") == "session_meta"
                            && metadata.RootElement.TryGetProperty("payload", out JsonElement payload)
                            && ReadString(payload, "id") == taskId;
                    }
                    catch (JsonException) { return false; }
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null)
            {
                _paths[taskId] = match;
            }
            return match;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<byte[]> ReverseCompleteLines(FileStream stream)
    {
        long completeEnd = CompleteJsonlSize(stream);
        long position = completeEnd;
        byte[] carry = [];
        while (position > 0)
        {
            int readSize = (int)Math.Min(ChunkSize, position);
            position -= readSize;
            stream.Position = position;
            var chunk = new byte[readSize];
            stream.ReadExactly(chunk);
            var combined = new byte[chunk.Length + carry.Length];
            Buffer.BlockCopy(chunk, 0, combined, 0, chunk.Length);
            Buffer.BlockCopy(carry, 0, combined, chunk.Length, carry.Length);

            int segmentEnd = combined.Length;
            int firstNewline = -1;
            for (int index = combined.Length - 1; index >= 0; index--)
            {
                if (combined[index] != (byte)'\n')
                {
                    continue;
                }
                firstNewline = index;
                int lineStart = index + 1;
                int lineLength = segmentEnd - lineStart;
                if (lineLength is > 0 and <= MaxLineBytes)
                {
                    var line = new byte[lineLength];
                    Buffer.BlockCopy(combined, lineStart, line, 0, lineLength);
                    yield return line;
                }
                segmentEnd = index;
            }

            int carryLength = firstNewline < 0 ? combined.Length : segmentEnd;
            carry = carryLength <= MaxLineBytes
                ? combined[..carryLength]
                : [];
        }
        if (carry.Length > 0)
        {
            yield return carry;
        }
    }

    private static long CompleteJsonlSize(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return 0;
        }
        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == (byte)'\n')
        {
            return stream.Length;
        }

        long position = stream.Length;
        var buffer = new byte[ChunkSize];
        while (position > 0)
        {
            int readSize = (int)Math.Min(buffer.Length, position);
            position -= readSize;
            stream.Position = position;
            stream.ReadExactly(buffer.AsSpan(0, readSize));
            for (int index = readSize - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return position + index + 1;
                }
            }
        }
        return 0;
    }

    private static string ReadString(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

internal sealed class CodexDesktopApprovalObserver
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, HashSet<string>> _approvalIds =
        new(StringComparer.Ordinal);

    internal async Task ObserveAsync(
        Func<IReadOnlyList<string>> taskIds,
        Action<string, bool> waitingChanged,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ObserveConnectionAsync(taskIds, waitingChanged, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                foreach (string taskId in _approvalIds.Keys.ToArray())
                {
                    waitingChanged(taskId, false);
                }
                _approvalIds.Clear();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task ObserveConnectionAsync(
        Func<IReadOnlyList<string>> taskIds,
        Action<string, bool> waitingChanged,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            "codex-ipc",
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(2000, cancellationToken);

        string initializeId = Guid.NewGuid().ToString();
        await WriteFrameAsync(pipe, new
        {
            type = "request",
            requestId = initializeId,
            sourceClientId = "initializing-client",
            version = 0,
            method = "initialize",
            @params = new { clientType = "feishu-bridge" },
        }, cancellationToken);
        using JsonDocument initialize = await ReadFrameAsync(pipe, cancellationToken);
        JsonElement initializeRoot = initialize.RootElement;
        if (ReadString(initializeRoot, "requestId") != initializeId
            || ReadString(initializeRoot, "resultType") != "success"
            || !initializeRoot.TryGetProperty("result", out JsonElement result)
            || ReadString(result, "clientId") is not { Length: > 0 } clientId)
        {
            throw new InvalidOperationException("Codex Desktop IPC 初始化未返回精确 clientId。" );
        }

        var followed = new HashSet<string>(StringComparer.Ordinal);
        Task<JsonDocument> pendingRead = ReadFrameAsync(pipe, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (string removed in followed.Except(taskIds(), StringComparer.Ordinal).ToArray())
            {
                await SetFollowingAsync(pipe, clientId, removed, false, cancellationToken);
                followed.Remove(removed);
                _approvalIds.Remove(removed);
                waitingChanged(removed, false);
            }
            foreach (string added in taskIds().Except(followed, StringComparer.Ordinal).ToArray())
            {
                await SetFollowingAsync(pipe, clientId, added, true, cancellationToken);
                followed.Add(added);
            }

            Task delay = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (await Task.WhenAny(pendingRead, delay) != pendingRead)
            {
                continue;
            }

            using JsonDocument frame = await pendingRead;
            if (ReadString(frame.RootElement, "type") == "client-discovery-request")
            {
                await WriteFrameAsync(pipe, new
                {
                    type = "client-discovery-response",
                    requestId = ReadString(frame.RootElement, "requestId"),
                    response = new { canHandle = false },
                }, cancellationToken);
            }
            HandleFrame(frame.RootElement, followed, waitingChanged);
            pendingRead = ReadFrameAsync(pipe, cancellationToken);
        }
    }

    private void HandleFrame(
        JsonElement frame,
        HashSet<string> followed,
        Action<string, bool> waitingChanged)
    {
        if (ReadString(frame, "type") == "client-discovery-request")
        {
            return;
        }
        if (ReadString(frame, "type") != "broadcast"
            || ReadString(frame, "method") != "thread-stream-state-changed"
            || !frame.TryGetProperty("params", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string taskId = ReadString(parameters, "conversationId");
        if (!followed.Contains(taskId)
            || !parameters.TryGetProperty("change", out JsonElement change)
            || change.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        HashSet<string> ids = _approvalIds.TryGetValue(taskId, out HashSet<string>? current)
            ? current
            : _approvalIds[taskId] = new HashSet<string>(StringComparer.Ordinal);
        if (ReadString(change, "type") == "snapshot")
        {
            ids.Clear();
        }
        CollectApprovalIds(change, ids);
        RemoveApprovalIds(change, ids);
        waitingChanged(taskId, ids.Count > 0);
    }

    private static void CollectApprovalIds(JsonElement value, HashSet<string> ids)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string method = ReadString(value, "method");
            if (method is "item/commandExecution/requestApproval"
                or "item/fileChange/requestApproval"
                or "item/permissions/requestApproval")
            {
                string id = ReadString(value, "id");
                if (id.Length == 0)
                {
                    id = ReadString(value, "requestId");
                }
                if (id.Length > 0)
                {
                    ids.Add(id);
                }
            }
            foreach (JsonProperty property in value.EnumerateObject())
            {
                CollectApprovalIds(property.Value, ids);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CollectApprovalIds(item, ids);
            }
        }
    }

    private static void RemoveApprovalIds(JsonElement change, HashSet<string> ids)
    {
        if (ReadString(change, "type") != "patches"
            || !change.TryGetProperty("patches", out JsonElement patches)
            || patches.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement patch in patches.EnumerateArray())
        {
            if (patch.ValueKind != JsonValueKind.Object
                || ReadString(patch, "op") != "remove"
                || !patch.TryGetProperty("path", out JsonElement path)
                || path.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            JsonElement[] segments = path.EnumerateArray().ToArray();
            if (segments.Length >= 2
                && segments[^2].ValueKind == JsonValueKind.String
                && segments[^2].GetString() == "requests"
                && segments[^1].ValueKind == JsonValueKind.String)
            {
                ids.Remove(segments[^1].GetString() ?? string.Empty);
            }
        }
    }

    private static Task SetFollowingAsync(
        Stream pipe,
        string clientId,
        string taskId,
        bool following,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(pipe, new
        {
            type = "broadcast",
            sourceClientId = clientId,
            version = 1,
            method = "thread-stream-following-changed",
            @params = new
            {
                conversationId = taskId,
                hostId = "local",
                following,
            },
        }, cancellationToken);

    private static async Task WriteFrameAsync(
        Stream stream,
        object message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException("Codex Desktop IPC 帧超过安全上限。" );
        }
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Codex Desktop IPC 返回了无效帧长度。" );
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }

    private static string ReadString(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
