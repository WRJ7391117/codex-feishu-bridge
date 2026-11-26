namespace DeepOriBridge.Models;

internal enum BridgeRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

internal sealed record BridgeRuntimeSnapshot(
    BridgeRuntimeState State,
    string Platform,
    int ThreadCount,
    long NotificationCount,
    string LastNotificationMethod,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public static BridgeRuntimeSnapshot Initial { get; } = new(
        BridgeRuntimeState.Stopped,
        string.Empty,
        0,
        0,
        string.Empty,
        null,
        DateTimeOffset.Now);
}
