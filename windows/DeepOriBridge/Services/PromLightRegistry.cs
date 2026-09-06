using System.Security.Cryptography;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record PromLightLamp(string Id, string DeviceId, string Owner, string Generation,
    string Name, bool IsDefault, string[] TaskIds, long Revision, bool PendingUnbind = false)
{
    internal PromLightBindingSnapshot Binding => new(Name, IsDefault, TaskIds.ToArray(), DateTimeOffset.UtcNow);
}

// One atomic registry owns identity, assignment, defaults and subscriptions. No
// discovery-order or display-name lookup is permitted for a write destination.
internal sealed class PromLightRegistry(string path,
    Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
{
    private sealed record Document(int Version, PromLightLamp[] Lamps);
    private readonly object _gate = new();
    // Shared by registry instances in this process. Configuration cannot commit
    // while the exact lamp has an outstanding physical write (including its ACK).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Lanes = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim Lane(string id) => Lanes.GetOrAdd(Path.GetFullPath(path) + ":" + id, _ => new(1, 1));
    internal async Task<IDisposable> AcquireWriteLeaseAsync(string id, CancellationToken token)
    {
        SemaphoreSlim lane = Lane(id);
        await lane.WaitAsync(token);
        return new WriteLease(lane);
    }
    private sealed class WriteLease(SemaphoreSlim lane) : IDisposable
    {
        private SemaphoreSlim? _lane = lane;
        public void Dispose() => Interlocked.Exchange(ref _lane, null)?.Release();
    }
    private readonly Func<byte[], byte[]> _protect = protect ?? (bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));

    internal PromLightLamp[] All() => Access(lamps => lamps.ToArray());
    internal PromLightLamp[] Owned(string owner, string generation) => All()
        .Where(l => l.Owner == owner && l.Generation == generation)
        .OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id).ToArray();
    internal PromLightLamp Require(string owner, string generation, string id, long? revision = null) =>
        Check(All(), owner, generation, id, revision);

    internal PromLightLamp Bind(string owner, string generation, string deviceId, string name, string[]? taskIds = null) => Access(lamps =>
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(generation) || string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidOperationException("需要明确的使用者、Bot 身份和设备。");
        if (lamps.Any(l => l.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该设备已被绑定或等待解绑确认；不能重新分配。");
        if (lamps.Count >= 64) throw new InvalidOperationException("提示灯数量已达到 64 盏上限。");
        string[] tasks = taskIds ?? [];
        if (tasks.Length > 50 || tasks.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128
            || t.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new InvalidOperationException("旧 Task 关联格式无效；原配置已保留。");
        var lamp = new PromLightLamp(Guid.NewGuid().ToString("N"), deviceId, owner, generation,
            NormalizeName(name), !lamps.Any(l => l.Owner == owner && l.Generation == generation && !l.PendingUnbind), tasks.Distinct().ToArray(), 1);
        lamps.Add(lamp);
        return lamp;
    }, true);

    internal PromLightLamp Update(string owner, string generation, string id, long revision,
        string? name = null, string[]? taskIds = null, bool makeDefault = false, bool unbind = false)
    {
        // Synchronous UI/card actions must not block the UI thread behind HID.
        // Busy is a failed mutation, never a successful unbind followed by an old frame.
        SemaphoreSlim lane = Lane(id);
        if (!lane.Wait(0)) throw new InvalidOperationException("提示灯写入尚未结束，配置未更改；请稍后重试。");
        using var lease = new WriteLease(lane);
        return Access(lamps =>
    {
        PromLightLamp previous = Check(lamps, owner, generation, id, revision);
        string[] tasks = taskIds ?? previous.TaskIds;
        if (tasks.Length > 50 || tasks.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128
            || t.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new InvalidOperationException("每盏灯最多关联 50 个有效 Task。");
        if (makeDefault)
            for (int i = 0; i < lamps.Count; i++)
                if (lamps[i].Owner == owner && lamps[i].Generation == generation && lamps[i].IsDefault && lamps[i].Id != id)
                    lamps[i] = lamps[i] with { IsDefault = false, Revision = lamps[i].Revision + 1 };
        PromLightLamp next = previous with { Name = name is null ? previous.Name : NormalizeName(name),
            TaskIds = unbind ? [] : tasks.Distinct(StringComparer.Ordinal).ToArray(),
            IsDefault = !unbind && (makeDefault || previous.IsDefault), PendingUnbind = unbind,
            Revision = previous.Revision + 1 };
        lamps[lamps.FindIndex(l => l.Id == id)] = next;
        EnsureDefault(lamps, owner, generation);
        return next;
    }, true);
    }

    // The ACK must belong to this exact cleanup revision and physical endpoint.
    internal bool CompleteUnbind(string id, string deviceId, long revision, PromLightStatus status, uint bytes) => Access(lamps =>
    {
        int index = lamps.FindIndex(l => l.Id == id && l.DeviceId == deviceId && l.Revision == revision
            && l.PendingUnbind && status == PromLightStatus.Idle && bytes > 0);
        if (index < 0) return false;
        PromLightLamp old = lamps[index];
        lamps.RemoveAt(index);
        EnsureDefault(lamps, old.Owner, old.Generation);
        return true;
    }, true);

    private static PromLightLamp Check(IEnumerable<PromLightLamp> lamps, string owner, string generation, string id, long? revision)
    {
        PromLightLamp? lamp = lamps.SingleOrDefault(l => l.Id == id && l.Owner == owner && l.Generation == generation);
        if (lamp is null || lamp.PendingUnbind || (revision.HasValue && lamp.Revision != revision.Value))
            throw new InvalidOperationException("提示灯不属于当前使用者、正在解绑或卡片已过期；请刷新后重试。");
        return lamp;
    }

    private static void EnsureDefault(List<PromLightLamp> lamps, string owner, string generation)
    {
        if (lamps.Any(l => l.Owner == owner && l.Generation == generation && l.IsDefault && !l.PendingUnbind)) return;
        int index = lamps.FindIndex(l => l.Owner == owner && l.Generation == generation && !l.PendingUnbind);
        if (index >= 0) lamps[index] = lamps[index] with { IsDefault = true, Revision = lamps[index].Revision + 1 };
    }

    private static string NormalizeName(string name)
    {
        string normalized = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 1 or > 40) throw new InvalidOperationException("灯名称必须为 1 到 40 个字符。");
        return normalized;
    }

    private T Access<T>(Func<List<PromLightLamp>, T> action, bool write = false)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Document document = File.Exists(path)
                ? JsonSerializer.Deserialize<Document>(_unprotect(File.ReadAllBytes(path)))
                    ?? throw new InvalidDataException("提示灯配置损坏；未改写原文件。")
                : new(2, []);
            if (document.Version != 2 || document.Lamps is null || document.Lamps.Length > 64
                || document.Lamps.Any(l => l is null || string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.DeviceId)
                    || string.IsNullOrWhiteSpace(l.Owner) || string.IsNullOrWhiteSpace(l.Generation) || l.TaskIds is null
                    || l.Revision < 1 || l.TaskIds.Length > 50 || (l.PendingUnbind && l.TaskIds.Length > 0))
                || document.Lamps.Select(l => l.Id).Distinct().Count() != document.Lamps.Length
                || document.Lamps.Select(l => l.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Lamps.Length
                || document.Lamps.Where(l => l.IsDefault).GroupBy(l => (l.Owner, l.Generation)).Any(g => g.Count() > 1))
                throw new InvalidDataException("提示灯配置版本或身份无效；已停止控制并保留原文件。");
            var lamps = document.Lamps.ToList();
            T result = action(lamps);
            if (write)
            {
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(_protect(JsonSerializer.SerializeToUtf8Bytes(new Document(2, lamps.ToArray()))));
                    stream.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            return result;
        }
    }
}
