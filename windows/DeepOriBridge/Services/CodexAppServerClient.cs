using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedReadOnlyThreads = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _processMessages = new();
    private Process? _process;
    private Task? _receiveTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private bool _disposed;

    public event Action<CodexAppServerNotification>? NotificationReceived;

    public event Action<Exception>? Disconnected;

    public CodexAppServerSessionInfo? SessionInfo { get; private set; }

    public bool IsConnected =>
        !_disposed
        && _process is { HasExited: false };

    internal int? ProcessId => _process is { HasExited: false } process
        ? process.Id
        : null;

    public async Task ConnectAsync(string executable, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("Codex app-server 会话已经启动。");
        }

        _process = StartAppServer(executable);
        _receiveTask = ReceiveLoopAsync(_process.StandardOutput, _lifetime.Token);
        _stderrTask = CaptureStderrAsync(_process.StandardError, _lifetime.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            JsonElement result = await RequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "deepori-bridge-windows",
                        title = "DeepOri Bridge for Windows",
                        version = "0.1.0",
                    },
                    capabilities = new { experimentalApi = false },
                },
                timeout.Token);

            SessionInfo = new CodexAppServerSessionInfo(
                ReadString(result, "platformFamily"),
                ReadString(result, "platformOs"));
            await SendNotificationAsync("initialized", new { }, timeout.Token);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<CodexThreadListResult> ListThreadsAsync(
        int limit,
        bool useStateDatabaseOnly,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        JsonElement result = await RequestAsync(
            "thread/list",
            new
            {
                limit,
                useStateDbOnly = useStateDatabaseOnly,
            },
            cancellationToken);

        int count = result.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        return new CodexThreadListResult(count);
    }

    public async Task<CodexThreadStartResult> StartThreadAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        string cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"Codex 工作目录不存在：{cwd}");
        }

        JsonElement result = await RequestAsync(
            "thread/start",
            new
            {
                cwd,
                approvalPolicy = "never",
                sandbox = "read-only",
            },
            cancellationToken);

        CodexThreadStartResult verified = ValidateThreadStartResponse(result, cwd);
        _verifiedReadOnlyThreads.TryAdd(verified.ThreadId, 0);
        return verified;
    }

    internal static CodexThreadStartResult ValidateThreadStartResponse(
        JsonElement result,
        string expectedWorkingDirectory)
    {
        string threadId = ReadNestedString(result, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codex thread/start 响应缺少 thread.id。");
        }

        string approvalPolicy = ReadString(result, "approvalPolicy");
        if (!string.Equals(approvalPolicy, "never", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codex thread/start 生效的 approvalPolicy 不是 never（实际：{SafePolicyLabel(approvalPolicy)}）；已阻止 turn/start。");
        }

        string sandboxType = result.TryGetProperty("sandbox", out JsonElement sandbox)
            && sandbox.ValueKind == JsonValueKind.Object
                ? ReadString(sandbox, "type")
                : string.Empty;
        if (!string.Equals(sandboxType, "readOnly", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codex thread/start 生效的 sandbox 不是 readOnly（实际：{SafePolicyLabel(sandboxType)}）；已阻止 turn/start。");
        }

        string actualCwd = ReadString(result, "cwd");
        string expectedCwd = Path.GetFullPath(expectedWorkingDirectory);
        if (string.IsNullOrWhiteSpace(actualCwd)
            || !string.Equals(
                Path.GetFullPath(actualCwd),
                expectedCwd,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Codex thread/start 生效的工作目录与本机授权目录不一致；已阻止 turn/start。");
        }

        return new CodexThreadStartResult(threadId, sandboxType, approvalPolicy);
    }

    public async Task<CodexTurnStartResult> StartTurnAsync(
        string threadId,
        string prompt,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Codex threadId 和测试提示词不能为空。");
        }

        if (!_verifiedReadOnlyThreads.ContainsKey(threadId))
        {
            throw new InvalidOperationException(
                "Codex thread 未通过服务端 readOnly/never 生效策略核验；已阻止 turn/start。");
        }

        JsonElement result = await RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[]
                {
                    new { type = "text", text = prompt },
                },
            },
            cancellationToken);
        string turnId = ReadNestedString(result, "turn", "id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("Codex turn/start 响应缺少 turn.id。");
        }

        string status = ReadNestedString(result, "turn", "status");
        return new CodexTurnStartResult(threadId, turnId, status);
    }

    public async Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            throw new ArgumentException("Codex threadId 和 turnId 不能为空。");
        }

        JsonElement result = await RequestAsync(
            "turn/interrupt",
            new
            {
                threadId,
                turnId,
            },
            cancellationToken);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex turn/interrupt 响应不是对象。");
        }
    }

    internal static bool IsTerminalTurnStatus(string status) =>
        string.Equals(status, "completed", StringComparison.Ordinal)
        || string.Equals(status, "interrupted", StringComparison.Ordinal)
        || string.Equals(status, "failed", StringComparison.Ordinal);

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("无法登记 Codex app-server 请求。");
        }

        try
        {
            await SendMessageAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters,
                }),
                cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        SendMessageAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }),
            cancellationToken);

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        Process process = _process is { HasExited: false } running
            ? running
            : throw new InvalidOperationException("Codex app-server stdio 会话未运行。");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw BuildProcessExitException();
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IReadOnlyList<JsonElement> messages;
                try
                {
                    messages = ParseStdioMessages(line);
                }
                catch (JsonException exception)
                {
                    int adjacentObjectBoundaries = line.Count(
                        character => character == '}');
                    throw new InvalidOperationException(
                        $"Codex app-server stdio framing 无效（长度 {line.Length}，对象结束符 {adjacentObjectBoundaries}，JSON 偏移 {exception.BytePositionInLine}，首尾字符 {(int)line[0]}/{(int)line[^1]}）；原始内容未记录。",
                        exception);
                }

                foreach (JsonElement message in messages)
                {
                    await DispatchMessageAsync(message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            Exception error = terminalError
                ?? new InvalidOperationException("Codex app-server stdio 会话已结束。");
            foreach (TaskCompletionSource<JsonElement> completion in _pending.Values)
            {
                completion.TrySetException(error);
            }

            if (terminalError is not null)
            {
                Disconnected?.Invoke(terminalError);
            }
        }
    }

    internal static IReadOnlyList<JsonElement> ParseStdioMessages(string payload)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(payload);
        var messages = new List<JsonElement>();
        int offset = 0;
        while (offset < utf8.Length)
        {
            while (offset < utf8.Length && char.IsWhiteSpace((char)utf8[offset]))
            {
                offset++;
            }

            if (offset >= utf8.Length)
            {
                break;
            }

            var reader = new Utf8JsonReader(
                utf8.AsSpan(offset),
                isFinalBlock: true,
                state: default);
            if (!reader.Read())
            {
                throw new JsonException("Codex app-server stdio payload 不包含完整 JSON 消息。");
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            messages.Add(document.RootElement.Clone());
            offset += checked((int)reader.BytesConsumed);
        }

        if (messages.Count == 0)
        {
            throw new JsonException("Codex app-server stdio payload 不包含 JSON 消息。");
        }

        return messages;
    }

    private async Task DispatchMessageAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (root.TryGetProperty("id", out JsonElement idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out long responseId)
            && _pending.TryGetValue(responseId, out TaskCompletionSource<JsonElement>? completion)
            && !root.TryGetProperty("method", out _))
        {
            if (root.TryGetProperty("error", out JsonElement error))
            {
                completion.TrySetException(
                    new InvalidOperationException($"Codex app-server 返回错误：{error.GetRawText()}"));
            }
            else if (root.TryGetProperty("result", out JsonElement result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(
                    new InvalidOperationException("Codex app-server 响应缺少 result。"));
            }

            return;
        }

        if (!root.TryGetProperty("method", out JsonElement methodElement))
        {
            return;
        }

        string method = methodElement.GetString() ?? string.Empty;
        if (root.TryGetProperty("id", out JsonElement requestId))
        {
            await RejectServerRequestAsync(requestId.Clone(), method, cancellationToken);
            return;
        }

        JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
            ? paramsElement.Clone()
            : default;
        NotificationReceived?.Invoke(
            new CodexAppServerNotification(method, parameters, DateTimeOffset.Now));
    }

    private Task RejectServerRequestAsync(
        JsonElement requestId,
        string method,
        CancellationToken cancellationToken) =>
        SendMessageAsync(
            BuildRejectedServerRequestResponse(requestId, method),
            cancellationToken);

    internal static string BuildRejectedServerRequestResponse(
        JsonElement requestId,
        string method)
    {
        object? result = method switch
        {
            "item/commandExecution/requestApproval" => new { decision = "cancel" },
            "item/fileChange/requestApproval" => new { decision = "cancel" },
            "execCommandApproval" => new { decision = "abort" },
            "applyPatchApproval" => new { decision = "abort" },
            "item/permissions/requestApproval" => new
            {
                permissions = new
                {
                    fileSystem = (object?)null,
                    network = (object?)null,
                },
                scope = (object?)null,
            },
            _ => null,
        };

        return result is not null
            ? JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                result,
            })
            : JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                error = new
                {
                    code = -32001,
                    message = "DeepOri Bridge rejects server-initiated input and tool requests.",
                },
            });
    }

    private async Task CaptureStderrAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                AppendProcessMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Process StartAppServer(string executable)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = utf8,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Codex app-server stdio 子进程。");
    }

    private void AppendProcessMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_processMessages)
        {
            if (_processMessages.Length < 4096)
            {
                _processMessages.AppendLine(message);
            }
        }
    }

    private Exception BuildProcessExitException()
    {
        string detail;
        lock (_processMessages)
        {
            detail = _processMessages.ToString().Trim();
        }

        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? "Codex app-server stdio 已关闭。"
                : $"Codex app-server stdio 已关闭：{detail}");
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Codex app-server stdio 尚未连接。");
        }
    }

    private static string SafePolicyLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "缺失" : value;

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadNestedString(
        JsonElement element,
        string containerName,
        string propertyName) =>
        element.TryGetProperty(containerName, out JsonElement container)
        && container.ValueKind == JsonValueKind.Object
        && container.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_process is { HasExited: false } running)
        {
            running.StandardInput.Close();
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
            }
        }

        if (_process is { HasExited: false } process)
        {
            using var processTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(processTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        if (_stderrTask is not null)
        {
            try
            {
                await _stderrTask;
            }
            catch
            {
            }
        }

        _verifiedReadOnlyThreads.Clear();
        _process?.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
    }
}
