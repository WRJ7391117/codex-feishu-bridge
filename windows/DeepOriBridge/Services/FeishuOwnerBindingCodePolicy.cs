using System.Security.Cryptography;
using System.Globalization;

namespace DeepOriBridge.Services;

internal readonly record struct FeishuOwnerBindingMatchResult(
    bool Matches,
    int TrimmedLength,
    bool IsEntireSixAsciiDigits,
    bool MatchesIgnoringExpiry,
    bool IsUnexpiredAtArrival);

internal static class FeishuOwnerBindingCodePolicy
{
    internal static string Generate() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);

    internal static bool Matches(
        string? expectedCode,
        DateTimeOffset? expiresAt,
        string? message,
        DateTimeOffset now) =>
        Evaluate(expectedCode, expiresAt, message, now).Matches;

    internal static FeishuOwnerBindingMatchResult Evaluate(
        string? expectedCode,
        DateTimeOffset? expiresAt,
        string? message,
        DateTimeOffset now)
    {
        string trimmed = message?.Trim() ?? string.Empty;
        bool isEntireSixAsciiDigits = IsSixAsciiDigits(trimmed);
        bool validExpectedCode = IsSixAsciiDigits(expectedCode);
        bool matchesIgnoringExpiry = validExpectedCode
            && isEntireSixAsciiDigits
            && string.Equals(trimmed, expectedCode, StringComparison.Ordinal);
        bool isUnexpiredAtArrival = expiresAt is DateTimeOffset expiry && expiry > now;
        return new FeishuOwnerBindingMatchResult(
            matchesIgnoringExpiry && isUnexpiredAtArrival,
            trimmed.Length,
            isEntireSixAsciiDigits,
            matchesIgnoringExpiry,
            isUnexpiredAtArrival);
    }

    private static bool IsSixAsciiDigits(string? value) =>
        value is { Length: 6 }
        && value.All(character => character is >= '0' and <= '9');
}
