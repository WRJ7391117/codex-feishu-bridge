using System.Globalization;
using System.Text;

namespace DeepOriBridge.Services;

internal static class FeishuEventSafetyPolicy
{
    internal static bool IsApprovedTarget(
        string? chatType,
        string? senderType,
        string? messageType,
        bool hasMessageId,
        bool hasSenderId) =>
        string.Equals(chatType, "p2p", StringComparison.Ordinal)
        && string.Equals(senderType, "user", StringComparison.Ordinal)
        && (string.Equals(messageType, "text", StringComparison.Ordinal)
            || string.Equals(messageType, "post", StringComparison.Ordinal))
        && hasMessageId
        && hasSenderId;

    internal static bool MatchesFixedCommand(string? content, string expected)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return string.Equals(
            CanonicalizeFixedCommand(content),
            CanonicalizeFixedCommand(expected),
            StringComparison.Ordinal);
    }

    internal static bool CanEnterControlledRouteGate(
        string approvedAction,
        bool approvedTarget,
        bool ownerMatches) =>
        approvedTarget
        && ownerMatches
        && (string.Equals(approvedAction, "codex-route-test", StringComparison.Ordinal)
            || string.Equals(approvedAction, "unmatched", StringComparison.Ordinal));

    private static string CanonicalizeFixedCommand(string value)
    {
        var canonical = new StringBuilder(value.Length);
        foreach (Rune rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)
                && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                canonical.Append(rune.ToString());
            }
        }

        return canonical.ToString();
    }
}
