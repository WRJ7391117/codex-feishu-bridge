namespace DeepOriBridge.Models;

public sealed record FeishuStoredCredential(
    string AppId,
    bool HasSecret,
    string? Error = null,
    bool HasVerifiedIdentity = false,
    string AppFingerprint = "",
    string BotDisplayName = "",
    string IdentityGeneration = "",
    string FullIdentityFingerprint = "");

public sealed record FeishuVerificationResult(
    bool Success,
    string Message,
    int? ExpiresInSeconds = null,
    string BotDisplayName = "",
    string IdentityFingerprint = "",
    bool CredentialWriteSucceeded = false,
    bool CurrentlyVerified = false);

internal sealed record FeishuRuntimeCredential(
    string AppId,
    string Secret,
    string BotDisplayName,
    string IdentityFingerprint,
    string IdentityGeneration);

public sealed record FeishuOwnerBindingState(
    bool IsBound,
    string? Error = null,
    int UserCount = 0,
    string ActiveUserFingerprint = "");

public sealed record FeishuProjectAuthorizationState(
    bool IsAuthorized,
    string ProjectPath,
    string? Error = null);
