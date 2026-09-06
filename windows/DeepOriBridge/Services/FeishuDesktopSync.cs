using System.Security.Cryptography;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record CodexDesktopSyncSnapshot(
    string Status,
    string TurnId,
    string AgentMessageText)
{
    internal static readonly CodexDesktopSyncSnapshot None = new("none", "", "");

    internal CodexControlledRouteResult? TerminalResult(string threadId) => Status switch
    {
        "completed" or "failed" or "interrupted" when TurnId.Length > 0 =>
            new CodexControlledRouteResult(threadId, TurnId, Status, AgentMessageText),
        _ => null,
    };
}

internal sealed record FeishuDesktopSyncSubscription(
    string Generation,
    string Sender,
    string ThreadId,
    string WorkingDirectory,
    string Title,
    string Project,
    string ProjectId,
    string ExpectedTurnId,
    string BaselineCompletedTurnId,
    DateTimeOffset CreatedAt);

// Only one explicit Desktop handoff may be pending for an owner and Bot
// generation. The record contains no prompt or credential and is protected by
// the current Windows user's DPAPI so a running result survives Bridge restart.
internal sealed class FeishuDesktopSyncSubscriptionStore(
    string directory,
    Func<byte[], byte[]>? protect = null,
    Func<byte[], byte[]>? unprotect = null)
{
    private readonly object _gate = new();
    private readonly Func<byte[], byte[]> _protect = protect ??
        (data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ??
        (data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));

    internal void Save(FeishuDesktopSyncSubscription subscription)
    {
        Validate(subscription);
        Access(entries =>
        {
            entries.RemoveAll(entry =>
                entry.CreatedAt < DateTimeOffset.UtcNow.AddDays(-7)
                || (entry.Generation == subscription.Generation
                    && entry.Sender == subscription.Sender));
            entries.Add(subscription);
            return true;
        }, write: true);
    }

    internal FeishuDesktopSyncSubscription[] Pending() => Access(entries =>
    {
        foreach (FeishuDesktopSyncSubscription entry in entries) Validate(entry);
        return entries.ToArray();
    });

    internal void Clear(string generation, string sender) => Access(entries =>
    {
        entries.RemoveAll(entry => entry.Generation == generation && entry.Sender == sender);
        return true;
    }, write: true);

    internal void Remove(FeishuDesktopSyncSubscription subscription) => Access(entries =>
    {
        entries.RemoveAll(entry =>
            entry.Generation == subscription.Generation
            && entry.Sender == subscription.Sender
            && entry.ThreadId == subscription.ThreadId
            && entry.ExpectedTurnId == subscription.ExpectedTurnId
            && entry.CreatedAt == subscription.CreatedAt);
        return true;
    }, write: true);

    private T Access<T>(Func<List<FeishuDesktopSyncSubscription>, T> action, bool write = false)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            using var lease = new FileStream(Path.Combine(directory, "subscriptions.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string path = Path.Combine(directory, "subscriptions.bin");
            List<FeishuDesktopSyncSubscription> entries = File.Exists(path)
                ? JsonSerializer.Deserialize<List<FeishuDesktopSyncSubscription>>(
                    _unprotect(File.ReadAllBytes(path)))
                    ?? throw new InvalidDataException("桌面接续记录无效；停止结果投递。")
                : [];
            T result = action(entries);
            if (write)
            {
                byte[] data = _protect(JsonSerializer.SerializeToUtf8Bytes(entries));
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            return result;
        }
    }

    private static void Validate(FeishuDesktopSyncSubscription subscription)
    {
        if (subscription.Generation.Length is < 1 or > 256
            || subscription.Sender.Length is < 1 or > 256
            || subscription.ThreadId.Length is < 1 or > 128
            || subscription.ProjectId.Length is < 1 or > 128
            || subscription.ExpectedTurnId.Length > 128
            || subscription.BaselineCompletedTurnId.Length > 128
            || !Path.IsPathFullyQualified(subscription.WorkingDirectory))
        {
            throw new InvalidDataException("桌面接续记录字段无效；停止结果投递。");
        }
    }
}

internal static class FeishuDesktopSyncDelivery
{
    internal static string EffectiveStatus(CodexThreadSummary task, CodexDesktopSyncSnapshot snapshot) =>
        snapshot.Status != "none"
            ? snapshot.Status
            : task.Status is "active" or "running" ? "running" : "none";

    internal static FeishuResultPart[] TextParts(
        string title,
        CodexControlledRouteResult result,
        Func<int, string> key)
    {
        string label = result.Status switch
        {
            "completed" => "桌面结果已同步",
            "failed" => "桌面运行失败",
            "interrupted" => "桌面运行已停止",
            _ => throw new InvalidOperationException("桌面接续结果尚未结束。"),
        };
        string text = string.IsNullOrWhiteSpace(result.AgentMessageText)
            ? result.Status == "completed"
                ? "Codex 已完成，但本轮没有文字结果，请在 Codex Desktop 查看产物。"
                : result.Status == "failed"
                    ? "Codex Desktop 中的这一轮运行失败。"
                    : "Codex Desktop 中的这一轮已停止。"
            : result.AgentMessageText;
        string safeTitle = title.Length > 100 ? title[..100] : title;
        string[] chunks = FeishuTaskFeedback.SplitUtf8(text).ToArray();
        return chunks.Select((chunk, index) => new FeishuResultPart(
            $"Task：{safeTitle} · {label}{(chunks.Length == 1 ? "" : $"（{index + 1}/{chunks.Length}）")}\n\n{chunk}",
            key(index))).ToArray();
    }
}

// A durable cursor distinguishes a first selection from a Bridge restart.
// It stores IDs only, scoped by Bot generation/owner/Task, never runnable input.
internal sealed class FeishuReplyCursorStore(string directory)
{
    private readonly object _gate = new();
    private string PathFor(string scope) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope))) + ".bin");
    internal string? Read(string scope)
    {
        lock (_gate)
        {
            string path = PathFor(scope);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<string>(ProtectedData.Unprotect(File.ReadAllBytes(path), null,
                DataProtectionScope.CurrentUser)) ?? throw new InvalidDataException("结果游标无效；未跳过结果。");
        }
    }
    internal void Save(string scope, string turnId)
    {
        if (turnId.Length > 128) throw new InvalidDataException("结果回合 ID 无效。");
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            string path = PathFor(scope);
            byte[] data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(turnId), null, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}

internal sealed class FeishuTaskSubscriptionStore(string directory)
{
    private readonly object _gate = new();
    private string PathFor(string scope) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope))) + ".bin");
    internal Dictionary<string, CurrentTaskSelection> Read(string scope)
    {
        lock (_gate)
        {
            string path = PathFor(scope);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, CurrentTaskSelection>>(ProtectedData.Unprotect(
                    File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))
                    ?? throw new InvalidDataException("订阅记录无效。")
                : new(StringComparer.Ordinal);
        }
    }
    internal void Set(string scope, string taskId, CurrentTaskSelection? selection)
    {
        lock (_gate)
        {
            var subscriptions = Read(scope);
            if (selection is null) subscriptions.Remove(taskId);
            else
            {
                if (selection.ThreadId != taskId || !Path.IsPathFullyQualified(selection.ProjectRoot)
                    || string.IsNullOrWhiteSpace(selection.DesktopProjectId)) throw new InvalidDataException("订阅目标无效。");
                if (!subscriptions.ContainsKey(taskId) && subscriptions.Count >= 50) throw new InvalidOperationException("最多订阅 50 个 Task。");
                subscriptions[taskId] = selection;
            }
            Directory.CreateDirectory(directory);
            string path = PathFor(scope);
            byte[] data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(subscriptions), null, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}
