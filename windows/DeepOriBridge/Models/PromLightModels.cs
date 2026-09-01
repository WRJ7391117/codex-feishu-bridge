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
