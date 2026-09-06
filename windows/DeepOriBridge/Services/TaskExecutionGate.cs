namespace DeepOriBridge.Services;

// One writer per Task, two independent Tasks by default (Mac parity).
// Same-Task waiters do not consume the global capacity of unrelated Tasks.
internal sealed class TaskExecutionGate(int maximum = 2)
{
    private readonly SemaphoreSlim _capacity = new(maximum, maximum);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed class Entry { internal readonly SemaphoreSlim Lock = new(1, 1); internal int References; }

    internal async Task<IDisposable> EnterAsync(string taskId, CancellationToken token)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(taskId, out entry!)) _entries.Add(taskId, entry = new());
            entry.References++;
        }
        bool ownsTask = false;
        try
        {
            await entry.Lock.WaitAsync(token);
            ownsTask = true;
            await _capacity.WaitAsync(token);
            return new Lease(() => { _capacity.Release(); entry.Lock.Release(); ReleaseReference(taskId, entry); });
        }
        catch
        {
            if (ownsTask) entry.Lock.Release();
            ReleaseReference(taskId, entry);
            throw;
        }
    }
    private void ReleaseReference(string taskId, Entry entry)
    {
        lock (_gate)
            if (--entry.References == 0) { _entries.Remove(taskId); entry.Lock.Dispose(); }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
