namespace DeepOriBridge.Models;

public sealed record FeishuStoredCredential(
    string AppId,
    bool HasSecret,
    string? Error = null,
    bool HasVerifiedIdentity = false,
    string AppFingerprint = "");

public sealed record FeishuVerificationResult(
    bool Success,
    string Message,
    int? ExpiresInSeconds = null);

public sealed record FeishuOwnerBindingState(
    bool IsBound,
    string? Error = null,
    int UserCount = 0,
    string ActiveUserFingerprint = "");

public sealed record FeishuProjectAuthorizationState(
    bool IsAuthorized,
    string ProjectPath,
    string? Error = null);
