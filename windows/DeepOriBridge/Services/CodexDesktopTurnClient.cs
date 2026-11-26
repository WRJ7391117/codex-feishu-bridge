using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

// Forward to the Desktop writer instead of acquiring a second thread/resume writer.
internal sealed class CodexDesktopTurnClient(string pipeName = "codex-ipc")
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    internal async Task<CodexControlledRouteResult?> TryRunAsync(
        string threadId, string prompt, string cwd,
        Action<string> started, Func<bool> stillAuthorized,
        CancellationToken cancellationToken,
        CodexRolloutTaskStateReader? rolloutReader = null,
        IReadOnlyList<CodexInputAttachment>? attachments = null,
        Action? onSubmitting = null, Action? onNotAccepted = null)
    {
        var reader = rolloutReader ?? new CodexRolloutTaskStateReader();
        // Capture the tail BEFORE submitting, so a fast completion cannot be lost.
        string? path = reader.ResolvePath(threadId);
        if (path is null) return null;
        long offset = new FileInfo(path).Length;
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            return null; // No start request has been sent; app-server fallback is safe.
        }

        string clientId;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            JsonElement initialize = await RequestAsync(pipe, "initializing-client", "initialize", 0,
                new { clientType = "feishu-bridge" }, handshake.Token);
            if (ReadString(initialize, "resultType") != "success"
                || !initialize.TryGetProperty("result", out JsonElement result)
                || ReadString(result, "clientId") is not { Length: > 0 } id)
                throw new InvalidOperationException("Codex Desktop 连接初始化失败；消息尚未发送。");
            clientId = id;
        }
        if (!stillAuthorized())
            throw new InvalidOperationException("当前 Task 或授权已变化；未发送消息。");

        string turnId;
        // A canceled queue claim happens BEFORE any start request is sent and
        // must not be disguised as an uncertain network acknowledgement.
        onSubmitting?.Invoke();
        try
        {
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirmation.CancelAfter(TimeSpan.FromSeconds(35));
            JsonElement response = await RequestAsync(pipe, clientId, "thread-follower-start-turn", 2,
                new
                {
                    conversationId = threadId,
                    turnStart = new
                    {
                        request = new { threadId, cwd, input = FeishuAttachments.BuildTurnInput(prompt, attachments) },
                        context = new { inheritThreadSettings = true, attachments = FeishuAttachments.BuildContextAttachments(attachments) },
                    },
                }, confirmation.Token);
            if (ReadString(response, "resultType") != "success")
            {
                string error = ReadString(response, "error");
                if (error == "no-client-found")
                {
                    // This is an explicit non-delivery, unlike a lost acknowledgement.
                    onNotAccepted?.Invoke();
                    if (onSubmitting is null || onNotAccepted is not null) return null;
                }
                if (error.Contains("active turn", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("already running", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("busy", StringComparison.OrdinalIgnoreCase))
                    throw new CodexTaskBusyException();
                onNotAccepted?.Invoke();
                throw new InvalidOperationException("Codex Desktop 未接受本条消息；请在桌面端查看当前 Task 状态。");
            }
            turnId = ReadStartedTurnId(response);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException)
        {
            // Never retry through app-server after an uncertain acknowledgement.
            throw new InvalidOperationException(
                "消息已向 Codex Desktop 提交，但未收到启动确认；为避免重复执行没有重发，请在桌面端查看。", exception);
        }

        started(turnId);
        try
        {
            return await WaitForCompletionAsync(path, offset, threadId, turnId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A Bridge deadline or connection loss is not a user's stop request.
            // The Desktop writer owns execution; keep its turn and inputs alive.
            return new(threadId, turnId, "pending",
                "桥接等待已结束或连接已断开，但没有停止 Codex，也没有重发指令。请在桌面端查看任务；保持当前 Task 关联后，完成结果会继续尝试回传。");
        }
    }

    internal static string ReadStartedTurnId(JsonElement response)
    {
        if (response.TryGetProperty("result", out JsonElement outer)
            && outer.TryGetProperty("result", out JsonElement result)
            && result.TryGetProperty("turn", out JsonElement turn)
            && ReadString(turn, "id") is { Length: > 0 } id)
            return id;
        throw new InvalidOperationException("Codex Desktop 已接受消息但没有返回 turn ID；没有重发，请在桌面端查看。");
    }

    internal static async Task<CodexControlledRouteResult> WaitForCompletionAsync(
        string path, long offset, string threadId, string turnId, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset;
        var pending = new List<byte>();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length < stream.Position)
                throw new IOException("Codex Task 记录已被替换；未重发消息。");
            int count = await stream.ReadAsync(buffer, cancellationToken);
            for (int index = 0; index < count; index++)
            {
                byte value = buffer[index];
                if (value != (byte)'\n')
                {
                    pending.Add(value);
                    if (pending.Count > 8 * 1024 * 1024)
                        throw new InvalidDataException("Codex Task 记录超过读取上限。");
                    continue;
                }
                try
                {
                    using JsonDocument document = JsonDocument.Parse(pending.ToArray());
                    JsonElement root = document.RootElement;
                    if (ReadString(root, "type") == "event_msg"
                        && root.TryGetProperty("payload", out JsonElement payload)
                        && ReadString(payload, "turn_id") == turnId)
                    {
                        string type = ReadString(payload, "type");
                        if (type is "task_complete" or "task_failed" or "turn_aborted")
                            return new(threadId, turnId,
                                type == "task_complete" ? "completed" : type == "turn_aborted" ? "interrupted" : "failed",
                                ReadString(payload, "last_agent_message"));
                    }
                }
                catch (JsonException) { /* Ignore malformed/unrelated records, never a partial line. */ }
                finally { pending.Clear(); }
            }
            if (count == 0) await Task.Delay(250, cancellationToken);
        }
    }

    internal static async Task<JsonElement> RequestAsync(
        Stream pipe, string clientId, string method, int version, object parameters,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString();
        await WriteFrameAsync(pipe, new
        {
            type = "request", requestId, sourceClientId = clientId, version, method,
            @params = parameters, timeoutMs = 30000,
        }, cancellationToken);
        while (true)
        {
            using JsonDocument document = await ReadFrameAsync(pipe, cancellationToken);
            JsonElement frame = document.RootElement;
            if (ReadString(frame, "type") == "client-discovery-request")
            {
                await WriteFrameAsync(pipe, new
                {
                    type = "client-discovery-response", requestId = ReadString(frame, "requestId"),
                    response = new { canHandle = false },
                }, cancellationToken);
            }
            if (ReadString(frame, "type") == "response" && ReadString(frame, "requestId") == requestId)
                return frame.Clone();
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, object message, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("IPC 帧过大。");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<JsonDocument> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is 0 or > MaxFrameBytes) throw new InvalidDataException("IPC 帧长度无效。");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return JsonDocument.Parse(payload);
    }

    private static string ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
