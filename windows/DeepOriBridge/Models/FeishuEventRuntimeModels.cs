namespace DeepOriBridge.Models;

public enum FeishuEventRuntimeState
{
    Stopped,
    Configuring,
    Connecting,
    Listening,
    Stopping,
    Faulted,
}

public sealed record FeishuEventRuntimeSnapshot(
    FeishuEventRuntimeState State,
    int ReceivedEventCount = 0,
    DateTimeOffset? LastReceivedAt = null,
    string? Error = null,
    bool ReplyArmed = false,
    int ReplySentCount = 0,
    DateTimeOffset? LastReplyAt = null,
    string? ReplyError = null,
    string? LastMatchDiagnostic = null,
    bool CodexRouteCaptureArmed = false,
    bool CodexRouteCapturePending = false,
    DateTimeOffset? LastCodexRouteCapturedAt = null,
    int CodexRouteReplySentCount = 0,
    DateTimeOffset? LastCodexRouteReplyAt = null,
    string? CodexRouteError = null,
    bool OwnerBound = false,
    bool OwnerBindingArmed = false,
    DateTimeOffset? LastOwnerBoundAt = null,
    string? OwnerBindingError = null,
    bool ReadOnlyRouteCaptureArmed = false,
    bool ReadOnlyRouteCapturePending = false,
    DateTimeOffset? LastReadOnlyRouteCapturedAt = null,
    string? ReadOnlyPromptPreview = null,
    int ReadOnlyRouteReplySentCount = 0,
    DateTimeOffset? LastReadOnlyRouteReplyAt = null,
    string? ReadOnlyRouteError = null,
    string? OwnerBindingCode = null,
    DateTimeOffset? OwnerBindingExpiresAt = null,
    int MenuEventCount = 0,
    int CardActionEventCount = 0);
