namespace DeepOriBridge.Services;

internal sealed class FeishuIdentityTransactionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeStart;
    private long _cancellationVersion;

    internal long CaptureStartVersion() => Volatile.Read(ref _cancellationVersion);

    internal Task WaitAsync(CancellationToken cancellationToken) =>
        _gate.WaitAsync(cancellationToken);

    internal async Task CancelStartAndWaitDurablyAsync()
    {
        CancelStart();
        await _gate.WaitAsync(CancellationToken.None);
    }

    internal CancellationTokenSource BeginStart(
        CancellationToken cancellationToken,
        long expectedVersion)
    {
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? replaced = Interlocked.Exchange(ref _activeStart, attempt);
        replaced?.Cancel();
        replaced?.Dispose();
        if (Volatile.Read(ref _cancellationVersion) != expectedVersion)
        {
            attempt.Cancel();
        }
        return attempt;
    }

    internal void EndStart(CancellationTokenSource? attempt)
    {
        if (attempt is null)
        {
            return;
        }

        if (ReferenceEquals(
                Interlocked.CompareExchange(ref _activeStart, null, attempt),
                attempt))
        {
            attempt.Dispose();
        }
    }

    internal void CancelStart()
    {
        Interlocked.Increment(ref _cancellationVersion);
        Volatile.Read(ref _activeStart)?.Cancel();
    }

    internal bool IsStartCancellationRequested =>
        Volatile.Read(ref _activeStart)?.IsCancellationRequested == true;

    internal void Release() => _gate.Release();
}
