using System.Security.Cryptography;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record PromLightTurnOrigin(string Generation, string Owner, string TaskId, string TurnId, DateTimeOffset At);

internal sealed class PromLightTurnOrigins(string path,
    Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
{
    private readonly object _gate = new();
    private readonly Func<byte[], byte[]> _protect = protect ?? (b => ProtectedData.Protect(b, null, DataProtectionScope.CurrentUser));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ?? (b => ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser));
    internal void Record(PromLightTurnOrigin origin) => Access(entries =>
    {
        if (new[] { origin.Generation, origin.Owner, origin.TaskId, origin.TurnId }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("提示灯运行来源记录缺少身份。");
        var existing = entries.SingleOrDefault(e => e.Generation == origin.Generation && e.TaskId == origin.TaskId && e.TurnId == origin.TurnId);
        if (existing is not null && existing.Owner != origin.Owner) throw new InvalidOperationException("同一轮的使用者来源冲突；未覆盖。");
        if (existing is null) entries.Add(origin);
        entries.RemoveAll(e => e.At < DateTimeOffset.UtcNow.AddDays(-14));
        if (entries.Count > 10000) entries.RemoveRange(0, entries.Count - 10000);
        return true;
    }, true);
    internal string? Find(string generation, string task, string turn) => Access(entries =>
        entries.SingleOrDefault(e => e.Generation == generation && e.TaskId == task && e.TurnId == turn)?.Owner);
    private T Access<T>(Func<List<PromLightTurnOrigin>, T> action, bool write = false)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var entries = File.Exists(path) ? JsonSerializer.Deserialize<List<PromLightTurnOrigin>>(_unprotect(File.ReadAllBytes(path)))
                ?? throw new InvalidDataException("提示灯来源记录损坏。") : [];
            T result = action(entries);
            if (write)
            {
                using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                { file.Write(_protect(JsonSerializer.SerializeToUtf8Bytes(entries))); file.Flush(true); }
                File.Move(path + ".tmp", path, true);
            }
            return result;
        }
    }
}

internal static class PromLightOwnerStatusPolicy
{
    internal static PromLightStatus Resolve(PromLightStatus taskStatus, string lampOwner, string? turnOwner, int subscribedOwners) =>
        taskStatus == PromLightStatus.HumanGate && (turnOwner is not null ? lampOwner != turnOwner : subscribedOwners != 1)
            ? PromLightStatus.Running : taskStatus;
}

internal sealed class PromLightRetryPolicy
{
    private int _failures;
    private PromLightStatus? _desired;
    internal DateTimeOffset NextAttempt { get; private set; }
    internal bool MayAttempt(PromLightStatus desired, DateTimeOffset now, bool explicitRefresh)
    {
        if (_desired != desired || explicitRefresh) { _desired = desired; _failures = 0; NextAttempt = now; }
        return now >= NextAttempt;
    }
    internal void Failed(DateTimeOffset now, double jitter)
    {
        _failures = Math.Min(6, _failures + 1);
        double seconds = Math.Min(60, Math.Pow(2, _failures)) * (0.8 + Math.Clamp(jitter, 0, 1) * 0.2);
        NextAttempt = now.AddSeconds(seconds);
    }
    internal void Succeeded() { _failures = 0; NextAttempt = DateTimeOffset.MinValue; }
}
