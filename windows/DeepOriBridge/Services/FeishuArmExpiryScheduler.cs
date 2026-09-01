namespace DeepOriBridge.Services;

internal sealed class FeishuArmExpiryScheduler : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<FeishuRouteKind, Entry> _current = new();
    private readonly HashSet<Entry> _active = new();
    private bool _disposed;
    private int _faultCount;

    internal int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active.Count;
            }
        }
    }

    internal int FaultCount => Volatile.Read(ref _faultCount);

    internal void Schedule(
        FeishuRouteKind kind,
        TimeSpan delay,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new Entry();
        Entry? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current.Remove(kind, out previous);
            _current[kind] = entry;
            _active.Add(entry);
            previous?.Cancellation.Cancel();
        }

        _ = RunAsync(kind, entry, delay, callback);
    }

    internal void Cancel(FeishuRouteKind kind)
    {
        Entry? entry;
        lock (_sync)
        {
            _current.Remove(kind, out entry);
            entry?.Cancellation.Cancel();
        }
    }

    internal void CancelAll()
    {
        Entry[] entries;
        lock (_sync)
        {
            entries = _current.Values.ToArray();
            _current.Clear();
            foreach (Entry entry in entries)
            {
                entry.Cancellation.Cancel();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _active.ToArray();
            _current.Clear();
            foreach (Entry entry in entries)
            {
                entry.Cancellation.Cancel();
            }
        }

        await Task.WhenAll(entries.Select(entry => entry.Completion.Task))
            .ConfigureAwait(false);
    }

    private async Task RunAsync(
        FeishuRouteKind kind,
        Entry entry,
        TimeSpan delay,
        Action callback)
    {
        try
        {
            await Task.Delay(delay, entry.Cancellation.Token).ConfigureAwait(false);
            callback();
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            Interlocked.Increment(ref _faultCount);
        }
        finally
        {
            lock (_sync)
            {
                if (_current.TryGetValue(kind, out Entry? current)
                    && ReferenceEquals(current, entry))
                {
                    _current.Remove(kind);
                }

                _active.Remove(entry);
            }

            entry.Cancellation.Dispose();
            entry.Completion.TrySetResult();
        }
    }

    private sealed class Entry
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
