namespace DeepOriBridge.Services;

internal static class FeishuRuntimeStartPolicy
{
    internal static async Task WaitForReadyAsync(
        Task ready,
        CancellationToken attemptToken,
        TimeSpan timeout)
    {
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        readyTimeout.CancelAfter(timeout);
        try
        {
            await ready.WaitAsync(readyTimeout.Token);
        }
        catch (OperationCanceledException) when (!attemptToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"飞书 WebSocket 长连接没有在 {timeout.TotalSeconds:0} 秒内进入 ready 状态。");
        }
    }
}
