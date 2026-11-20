using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record FeishuQueuedInput(
    string Id, string MessageId, string Sender, string Generation,
    CurrentTaskSelection Selection, CodexThreadSummary Task, string Prompt, string? ResourceJson,
    DateTimeOffset CreatedAt, string Phase = "queued", string TurnId = "", string CardId = "",
    DateTimeOffset? AvailableAt = null, string AuthorizationScope = "");

// Persist acceptance BEFORE acknowledging or submitting. Only queued entries are
// replayable; submitting is deliberately not replayable after an uncertain start.
internal sealed class FeishuTaskInputQueue(string directory,
    Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
{
    private readonly object _gate = new();
    private readonly Func<byte[], byte[]> _protect = protect ??
        (data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ??
        (data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
    internal const int MaxPending = 50;
    internal const int MaxPerTask = 10;

    internal static string Key(string generation, string sender, string messageId) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { generation, sender, messageId })));

    internal (bool Added, int Position) Enqueue(FeishuQueuedInput input) => Access(entries =>
    {
        if (entries.Any(e => e.Id == input.Id)) return (false, 0);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        entries.RemoveAll(e => e.Phase == "finished" && e.CreatedAt < cutoff);
        if (entries.Count >= 10000)
            throw new InvalidOperationException("消息去重记录已满；未接收本条指令，请稍后再试。");
        FeishuQueuedInput[] pending = entries.Where(e => e.Phase == "queued").ToArray();
        if (pending.Length >= MaxPending)
            throw new InvalidOperationException("桥接消息队列已满，请稍后重试。");
        int position = pending.Count(e => e.Selection.ThreadId == input.Selection.ThreadId) + 1;
        if (position > MaxPerTask)
            throw new InvalidOperationException($"该 Task 已有 {MaxPerTask} 条待处理消息，请等待后再试。");
        entries.Add(input);
        return (true, position);
    }, write: true);

    internal FeishuQueuedInput[] Pending() => Access(entries => entries.Where(e => e.Phase != "finished").ToArray());

    // Retire only queued work. An accepted/uncertain turn still owns its inputs.
    internal int RetireQueued(Func<FeishuQueuedInput, bool> invalid) => Access(entries =>
    {
        int retired = 0;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Phase == "queued" && invalid(entries[i]))
            {
                entries[i] = entries[i] with { Phase = "finished", Prompt = "", ResourceJson = null };
                retired++;
            }
        return retired;
    }, write: true);

    internal static IEnumerable<FeishuQueuedInput> Heads(IEnumerable<FeishuQueuedInput> pending) =>
        pending.Where(e => e.Phase != "finished").GroupBy(e => e.Selection.ThreadId).Select(g => g.First());

    internal void RequeueBusy(string id) => Access(entries =>
    {
        int index = entries.FindIndex(e => e.Id == id && e.Phase is "queued" or "submitting");
        if (index >= 0) entries[index] = entries[index] with { Phase = "queued", AvailableAt = DateTimeOffset.UtcNow.AddSeconds(15) };
        return true;
    }, write: true);

    internal bool Transition(string id, string from, string to, string? turnId = null) => Access(entries =>
    {
        int index = entries.FindIndex(e => e.Id == id && e.Phase == from);
        if (index < 0) return false;
        FeishuQueuedInput entry = entries[index];
        entries[index] = entry with { Phase = to, TurnId = turnId ?? entry.TurnId };
        if (to == "finished") entries[index] = entries[index] with { Prompt = "", ResourceJson = null };
        return true;
    }, write: true);

    internal void SetCard(string id, string cardId) => Access(entries =>
    {
        int index = entries.FindIndex(e => e.Id == id);
        if (index >= 0) entries[index] = entries[index] with { CardId = cardId };
        return true;
    }, write: true);

    private T Access<T>(Func<List<FeishuQueuedInput>, T> action, bool write = false)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            // A second bridge process cannot read an old snapshot then overwrite
            // acceptance. Contention fails closed and never permits submission.
            using var lease = new FileStream(Path.Combine(directory, "queue.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            string path = Path.Combine(directory, "queue.bin");
            List<FeishuQueuedInput> entries = File.Exists(path)
                ? JsonSerializer.Deserialize<List<FeishuQueuedInput>>(_unprotect(File.ReadAllBytes(path)))
                    ?? throw new InvalidDataException("消息队列记录无效；未执行新指令。")
                : [];
            T result = action(entries);
            if (write)
            {
                byte[] data = _protect(JsonSerializer.SerializeToUtf8Bytes(entries));
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            return result;
        }
    }
}

internal sealed class CodexTaskBusyException() : InvalidOperationException("上一轮尚未结束，本条指令已排队。");
internal sealed class FeishuQueueInputCanceledException() : OperationCanceledException("排队消息已取消或已被其他提交者领取。");
