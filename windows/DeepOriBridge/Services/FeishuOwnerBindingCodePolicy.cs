using System.Security.Cryptography;

namespace DeepOriBridge.Services;

internal static class FeishuOwnerBindingCodePolicy
{
    internal static string Generate() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    internal static bool Matches(
        string? expectedCode,
        DateTimeOffset? expiresAt,
        string? message,
        DateTimeOffset now) =>
        expectedCode is { Length: 6 }
        && expiresAt > now
        && FeishuEventSafetyPolicy.MatchesFixedCommand(message, expectedCode);
}
