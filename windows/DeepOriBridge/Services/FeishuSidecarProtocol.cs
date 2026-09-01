using System.Text.Json;

namespace DeepOriBridge.Services;

internal static class FeishuSidecarProtocol
{
    public const string ReplyCommandType = "reply";
    public const string CommandResultType = "command_result";
    public const string SucceededState = "succeeded";

    public static string BuildReplyCommandJson(
        string requestId,
        string messageId,
        string replyText,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(messageId)
            || string.IsNullOrWhiteSpace(replyText)
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("飞书 sidecar 回复命令字段不能为空。");
        }

        return JsonSerializer.Serialize(new
        {
            type = ReplyCommandType,
            request_id = requestId,
            message_id = messageId,
            text = replyText,
            idempotency_key = idempotencyKey,
        });
    }

    public static bool TryReadCommandResult(
        JsonElement root,
        out string requestId,
        out bool succeeded)
    {
        requestId = string.Empty;
        succeeded = false;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out JsonElement type)
            || !string.Equals(type.GetString(), CommandResultType, StringComparison.Ordinal)
            || !root.TryGetProperty("request_id", out JsonElement request)
            || request.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("state", out JsonElement state)
            || state.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        requestId = request.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        succeeded = string.Equals(
            state.GetString(),
            SucceededState,
            StringComparison.Ordinal);
        return true;
    }
}
