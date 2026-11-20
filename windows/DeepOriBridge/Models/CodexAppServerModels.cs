using System.Text.Json;

namespace DeepOriBridge.Models;

internal sealed class CodexAppServerRequestException(int code, string rpcMessage)
    : InvalidOperationException($"Codex app-server 返回错误：{rpcMessage}")
{
    internal int Code { get; } = code;
    internal string RpcMessage { get; } = rpcMessage;
}

internal sealed record CodexAppServerSessionInfo(
    string PlatformFamily,
    string PlatformOs)
{
    public string Platform => $"{PlatformFamily}/{PlatformOs}".Trim('/');
}

internal sealed record CodexThreadListResult(int Count);

internal sealed record CodexThreadSummary(
    string ThreadId,
    string Title,
    string Preview,
    string WorkingDirectory,
    string Model,
    string Status,
    IReadOnlyList<string> ActiveFlags,
    string ReasoningEffort = "",
    string ServiceTier = "default",
    string ProjectId = "",
    string ThreadSource = "",
    bool Archived = false,
    bool AuthorizedForRouting = true,
    string ProjectDisplayName = "",
    string DesktopAssignedProjectId = "")
{
    // Preserve the raw server field; legacy membership is an explicit, separately verified projection.
    internal string EffectiveProjectId => string.IsNullOrWhiteSpace(ProjectId) ? DesktopAssignedProjectId : ProjectId;
}

internal sealed record CodexServiceTierSummary(string Id, string Name, string Description);

internal sealed record CodexModelSummary(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string>? ReasoningEfforts = null,
    IReadOnlyList<CodexServiceTierSummary>? ServiceTiers = null)
{
    public IReadOnlyList<string> AvailableReasoningEfforts => ReasoningEfforts ?? Array.Empty<string>();
    public IReadOnlyList<CodexServiceTierSummary> AvailableServiceTiers => ServiceTiers ?? Array.Empty<CodexServiceTierSummary>();
}

internal sealed record CodexThreadStartResult(
    string ThreadId,
    string SandboxType,
    string ApprovalPolicy);

internal sealed record CodexTurnStartResult(
    string ThreadId,
    string TurnId,
    string Status);

internal sealed record CodexControlledRouteResult(
    string ThreadId,
    string TurnId,
    string Status,
    string AgentMessageText);

internal sealed record CodexAppServerNotification(
    string Method,
    JsonElement Parameters,
    DateTimeOffset ReceivedAt);

internal enum CodexUsageAvailability
{
    Loading,
    Fresh,
    Stale,
    Unavailable,
}

// This projection intentionally excludes account identity and the raw app-server payload.
internal sealed record CodexUsageWindow(
    string Label,
    double UsedPercent,
    double RemainingPercent,
    int WindowDurationMins,
    DateTimeOffset? ResetsAt);

internal sealed record CodexUsageBucket(
    string LimitId,
    string LimitName,
    string PlanType,
    IReadOnlyList<CodexUsageWindow> Windows);

internal sealed record CodexUsageSnapshot(
    CodexUsageAvailability Availability,
    IReadOnlyList<CodexUsageBucket> Buckets,
    DateTimeOffset? UpdatedAt)
{
    public static CodexUsageSnapshot Initial { get; } = new(
        CodexUsageAvailability.Unavailable,
        Array.Empty<CodexUsageBucket>(),
        null);
}

internal static class CodexUsageSnapshotPolicy
{
    public static CodexUsageSnapshot AfterReadFailure(CodexUsageSnapshot current) =>
        current.Buckets.Count > 0
            ? current with { Availability = CodexUsageAvailability.Stale }
            : CodexUsageSnapshot.Initial;

    // A new app-server session is a new account boundary; never carry prior account data forward.
    public static CodexUsageSnapshot ForNewAppServerSession() => CodexUsageSnapshot.Initial;
}
