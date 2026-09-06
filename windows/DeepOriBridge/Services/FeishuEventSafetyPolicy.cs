using System.Text;
using System.Text.Json;

namespace DeepOriBridge.Services;

internal static class FeishuEventSafetyPolicy
{
    internal readonly record struct HarmlessReplyTestAnalysis(
        bool Matched,
        int MatchedTokenCount,
        int ExpectedTokenCount);

    internal static bool IsApprovedTarget(
        string? chatType,
        string? senderType,
        string? messageType,
        bool hasMessageId,
        bool hasSenderId) =>
        string.Equals(chatType, "p2p", StringComparison.Ordinal)
        && string.Equals(senderType, "user", StringComparison.Ordinal)
        && string.Equals(messageType, "text", StringComparison.Ordinal)
        && hasMessageId
        && hasSenderId;

    internal static bool TryExtractCanonicalText(
        string? messageType,
        string? rawContent,
        out string text,
        out string contentState)
    {
        text = string.Empty;
        contentState = "rejected";
        if (!string.Equals(messageType, "text", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(rawContent)
            || rawContent.Length > 16 * 1024)
        {
            return false;
        }

        string candidate = rawContent;
        if (rawContent.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(rawContent);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                int properties = 0;
                JsonElement textElement = default;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    properties++;
                    if (!string.Equals(property.Name, "text", StringComparison.Ordinal)
                        || property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }
                    textElement = property.Value;
                }
                if (properties != 1)
                {
                    return false;
                }
                candidate = textElement.GetString() ?? string.Empty;
                contentState = "text-json-decoded-in-app";
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else
        {
            contentState = "text-plain";
        }

        candidate = candidate.Trim();
        if (candidate.Length == 0 || candidate.Length > 8 * 1024)
        {
            text = string.Empty;
            contentState = "rejected";
            return false;
        }
        text = candidate;
        return true;
    }

    internal static bool TryExtractReadOnlyPrompt(string? content, out string prompt)
    {
        prompt = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string normalized = content.Normalize(NormalizationForm.FormKC);
        const string prefixLetters = "WindowsCodex";
        int index = 0;
        int prefixIndex = 0;
        while (index < normalized.Length && prefixIndex < prefixLetters.Length)
        {
            char current = normalized[index++];
            if (!char.IsLetterOrDigit(current))
            {
                continue;
            }

            if (char.ToUpperInvariant(current)
                != char.ToUpperInvariant(prefixLetters[prefixIndex]))
            {
                return false;
            }

            prefixIndex++;
        }

        if (prefixIndex != prefixLetters.Length)
        {
            return false;
        }

        // The route namespace is already protected by owner-only P2P and explicit
        // project authorization gates. Ignore Feishu presentation punctuation and
        // format/control characters after it, then preserve the prompt itself.
        while (index < normalized.Length && !char.IsLetterOrDigit(normalized[index]))
        {
            index++;
        }

        prompt = normalized[index..].Trim();
        return prompt.Length > 0;
    }

    internal static bool MatchesFixedCommand(string? content, string expected)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return string.Equals(
            CanonicalizeFixedCommand(content),
            CanonicalizeFixedCommand(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesHarmlessReplyTest(string? content, string expected)
        => AnalyzeHarmlessReplyTest(content, expected).Matched;

    internal static HarmlessReplyTestAnalysis AnalyzeHarmlessReplyTest(
        string? content,
        string expected)
    {
        const int maximumEnvelopeLength = 512;
        if (string.IsNullOrWhiteSpace(content)
            || content.Length > maximumEnvelopeLength)
        {
            return new(false, 0, 0);
        }

        string canonicalContent = CanonicalizeFixedCommand(content);
        string[] expectedTokens = expected
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(CanonicalizeFixedCommand)
            .Where(token => token.Length > 0)
            .ToArray();
        if (expectedTokens.Length == 0)
        {
            return new(false, 0, 0);
        }

        int searchStart = 0;
        int matchedTokens = 0;
        foreach (string token in expectedTokens)
        {
            int index = canonicalContent.IndexOf(
                token,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            matchedTokens++;
            searchStart = index + token.Length;
        }

        return new(
            matchedTokens == expectedTokens.Length,
            matchedTokens,
            expectedTokens.Length);
    }

    internal static bool CanEnterControlledRouteGate(
        string approvedAction,
        bool approvedTarget,
        bool ownerMatches) =>
        approvedTarget
        && ownerMatches
        && (string.Equals(approvedAction, "codex-route-test", StringComparison.Ordinal)
            || string.Equals(approvedAction, "unmatched", StringComparison.Ordinal));

    internal static bool CanSendUnmatchedOwnerHelp(
        string approvedAction,
        bool approvedTarget,
        bool ownerMatches,
        bool controlledCaptured,
        bool readOnlyCaptured) =>
        approvedTarget
        && ownerMatches
        && !controlledCaptured
        && !readOnlyCaptured
        && string.Equals(approvedAction, "unmatched", StringComparison.Ordinal);

    private static string CanonicalizeFixedCommand(string value)
    {
        var canonical = new StringBuilder(value.Length);
        foreach (Rune rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                canonical.Append(rune.ToString());
            }
        }

        return canonical.ToString();
    }
}
