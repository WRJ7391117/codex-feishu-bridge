using System.Text.Json;

namespace DeepOriBridge.Models;

internal sealed record CodexAppServerSessionInfo(
    string PlatformFamily,
    string PlatformOs)
{
    public string Platform => $"{PlatformFamily}/{PlatformOs}".Trim('/');
}

internal sealed record CodexThreadListResult(int Count);

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
