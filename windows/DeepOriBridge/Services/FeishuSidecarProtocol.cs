using System.Text.Json;

namespace DeepOriBridge.Services;

internal sealed record FeishuSidecarReadyExpectation(
    string RuntimeNonce,
    string IdentityFingerprint,
    string IdentityGeneration,
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    string ParentExecutableSha256,
    string ParentPayloadManifestSha256,
    IReadOnlyList<string> RegisteredConsumers,
    string SidecarExecutableSha256);

internal sealed record FeishuSidecarCommandResult(
    bool Succeeded,
    string ErrorStage,
    int? ErrorCode,
    string Operation = "",
    string MenuStage = "",
    string MenuDiff = "",
    string VersionState = "",
    string Version = "",
    string ErrorDiagnostic = "",
    string MessageId = "");

internal static class FeishuSidecarProtocol
{
    internal static readonly string[] ExpectedConsumers =
    [
        "im.message.receive_v1",
        "application.bot.menu_v6",
        "card.action.trigger",
    ];
    public const string ReplyCommandType = "reply";
    public const string CommandResultType = "command_result";
    public const string SucceededState = "succeeded";
    public const string SendP2PCommandType = "send_p2p";
    public const string SendCardCommandType = "send_card";
    public const string MenuInspectCommandType = "menu_inspect";
    public const string MenuInspectApplyCommandType = "menu_inspect_apply";
    public const string MenuPublishCommandType = "menu_publish";
    public const string MenuStatusCommandType = "menu_status";

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

    public static string BuildSendP2PCommandJson(
        string requestId,
        string recipientId,
        string replyText,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(recipientId)
            || string.IsNullOrWhiteSpace(replyText)
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("飞书 sidecar 单聊命令字段不能为空。");
        }

        return JsonSerializer.Serialize(new
        {
            type = SendP2PCommandType,
            request_id = requestId,
            recipient_id = recipientId,
            text = replyText,
            idempotency_key = idempotencyKey,
        });
    }

    public static string BuildMenuCommandJson(
        string commandType,
        string requestId,
        string expectedIdentityGeneration)
    {
        if (commandType is not (MenuInspectCommandType
                or MenuInspectApplyCommandType
                or MenuPublishCommandType
                or MenuStatusCommandType)
            || requestId.Length != 32
            || expectedIdentityGeneration.Length != 32)
        {
            throw new ArgumentException("飞书菜单命令或身份世代无效。");
        }

        return JsonSerializer.Serialize(new
        {
            type = commandType,
            request_id = requestId,
            expected_identity_generation = expectedIdentityGeneration,
        });
    }

    public static bool TryReadCommandResult(
        JsonElement root,
        out string requestId,
        out FeishuSidecarCommandResult result)
    {
        requestId = string.Empty;
        result = new(false, "unknown", null);
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

        bool succeeded = string.Equals(
            state.GetString(),
            SucceededState,
            StringComparison.Ordinal);
        string errorStage = "unknown";
        int? errorCode = null;
        if (!succeeded)
        {
            if (root.TryGetProperty("error_stage", out JsonElement stage)
                && stage.ValueKind == JsonValueKind.String
                && stage.GetString() is string parsedStage
                && parsedStage is "encoding" or "transport" or "api"
                    or "download_api" or "download_transport" or "download_encoding" or "download_io"
                    or "upload_api" or "upload_transport" or "upload_encoding"
                    or "send_api" or "send_transport" or "send_encoding"
                    or "size_limit" or "timeout" or "validation" or "unavailable"
                    or "path" or "write" or "read" or "busy" or "idempotency_conflict" or "idempotency_capacity")
            {
                errorStage = parsedStage;
            }

            if (root.TryGetProperty("error_code", out JsonElement code)
                && code.TryGetInt32(out int parsedCode)
                && parsedCode != 0)
            {
                errorCode = parsedCode;
            }
        }

        static string ReadSafeString(JsonElement element, string name, int maxLength = 128) =>
            element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is string parsed
            && parsed.Length <= maxLength
                ? parsed
                : string.Empty;
        result = new(
            succeeded,
            errorStage,
            errorCode,
            ReadSafeString(root, "operation"),
            ReadSafeString(root, "menu_stage"),
            ReadSafeString(root, "menu_diff"),
            ReadSafeString(root, "version_state"),
            ReadSafeString(root, "version", 50),
            ReadSafeDiagnostic(root),
            ReadSafeString(root, "message_id", 256));
        return true;

        static string ReadSafeDiagnostic(JsonElement element)
        {
            string diagnostic = ReadSafeString(element, "error_diagnostic", 512);
            return diagnostic.Length > 0
                && diagnostic.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '_' or '-' or '.' or '[' or ']' or ':' or ';' or ',')
                    ? diagnostic
                    : string.Empty;
        }
    }

    public static string BuildSendCardCommandJson(string requestId, string recipientId, string cardJson, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(recipientId)
            || string.IsNullOrWhiteSpace(cardJson) || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("飞书 sidecar 卡片命令字段不能为空。");
        }
        return JsonSerializer.Serialize(new { type = SendCardCommandType, request_id = requestId, recipient_id = recipientId, card = cardJson, idempotency_key = idempotencyKey });
    }

    internal static bool MatchesReadyAttestation(
        JsonElement root,
        FeishuSidecarReadyExpectation expected)
    {
        static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        int parentPid = root.TryGetProperty("parent_pid", out JsonElement parentPidElement)
            && parentPidElement.TryGetInt32(out int parsedParentPid)
                ? parsedParentPid
                : 0;
        long parentStart = root.TryGetProperty(
            "parent_start_time_utc_ticks",
            out JsonElement parentStartElement)
            && parentStartElement.TryGetInt64(out long parsedParentStart)
                ? parsedParentStart
                : 0;
        string[] consumers = root.TryGetProperty("registered_consumers", out JsonElement consumerElement)
            && consumerElement.ValueKind == JsonValueKind.Array
                ? consumerElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray()
                : [];
        bool exactConsumers = consumers.Length == ExpectedConsumers.Length
            && consumers.Distinct(StringComparer.Ordinal).Count() == ExpectedConsumers.Length
            && consumers.SequenceEqual(ExpectedConsumers, StringComparer.Ordinal)
            && expected.RegisteredConsumers.SequenceEqual(
                ExpectedConsumers,
                StringComparer.Ordinal);
        return FileAttestation.FixedTimeEquals(
                ReadString(root, "runtime_nonce"),
                expected.RuntimeNonce)
            && FileAttestation.FixedTimeEquals(
                ReadString(root, "identity_fingerprint"),
                expected.IdentityFingerprint)
            && FileAttestation.FixedTimeEquals(
                ReadString(root, "identity_generation"),
                expected.IdentityGeneration)
            && parentPid == expected.ParentProcessId
            && parentStart == expected.ParentStartTimeUtcTicks
            && FileAttestation.FixedTimeEquals(
                ReadString(root, "parent_executable_sha256"),
                expected.ParentExecutableSha256)
            && FileAttestation.FixedTimeEquals(
                ReadString(root, "parent_payload_manifest_sha256"),
                expected.ParentPayloadManifestSha256)
            && exactConsumers
            && FileAttestation.FixedTimeEquals(
                ReadString(root, "sidecar_executable_sha256"),
                expected.SidecarExecutableSha256);
    }
}
