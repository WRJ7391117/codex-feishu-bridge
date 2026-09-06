namespace DeepOriBridge.Models;

internal enum PromLightStatus
{
    Idle,
    Running,
    HumanGate,
    Error,
}

internal sealed record PromLightDeviceSnapshot(
    bool Found,
    bool Opened,
    int DeviceCount,
    string Product,
    string DeviceVersion,
    int ReleaseNumber,
    string? Error,
    DateTimeOffset CheckedAt);

internal sealed record PromLightWriteResult(
    PromLightStatus Status,
    uint BytesWritten,
    DateTimeOffset SubmittedAt);

internal sealed record PromLightActivitySnapshot(
    PromLightStatus Status,
    uint BytesWritten,
    DateTimeOffset? SubmittedAt)
{
    internal static PromLightActivitySnapshot Initial { get; } = new(
        PromLightStatus.Idle,
        0,
        null);
}

internal sealed record PromLightBindingSnapshot(
    string Name,
    bool IsDefault,
    IReadOnlyList<string> TaskIds,
    DateTimeOffset UpdatedAt)
{
    internal static PromLightBindingSnapshot Initial { get; } = new(
        "PromLight",
        true,
        Array.Empty<string>(),
        DateTimeOffset.MinValue);
}

internal sealed record PromLightObservedTaskState(
    string TaskId,
    PromLightStatus Status,
    string Source,
    DateTimeOffset ObservedAt);
