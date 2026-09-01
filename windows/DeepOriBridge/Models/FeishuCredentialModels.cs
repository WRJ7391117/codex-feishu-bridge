namespace DeepOriBridge.Models;

public sealed record FeishuStoredCredential(
    string AppId,
    bool HasSecret,
    string? Error = null);

public sealed record FeishuVerificationResult(
    bool Success,
    string Message,
    int? ExpiresInSeconds = null);

public sealed record FeishuOwnerBindingState(
    bool IsBound,
    string? Error = null);
