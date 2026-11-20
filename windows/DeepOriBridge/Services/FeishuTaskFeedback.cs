using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record FeishuTaskFeedbackOutcome(bool TaskSucceeded, bool Delivered, string? Error, bool Pending = false);

// Throw only for input rejected before submission to Codex. The message is sent to the user.
// A missing running callback does not prove that execution never started.
internal sealed class FeishuInputRejectedException(string message) : InvalidOperationException(message);

// One source message owns one progress card and one idempotent sequence of results.
// The execution delegate is invoked exactly once; delivery retries never rerun Codex.
internal sealed class FeishuTaskFeedback(
    Func<string, CancellationToken, Task<string>> createCard,
    Func<string, string, CancellationToken, Task> patchCard,
    Func<string, string, CancellationToken, Task> sendText,
    TimeSpan? pulseInterval = null,
    TimeSpan? retryDelay = null,
    Func<FeishuResultPart[], CancellationToken, Task>? prepareResults = null,
    bool durableQueue = false)
{
    internal async Task<FeishuTaskFeedbackOutcome> RunAsync(
        string title, string project,
        Func<Action, CancellationToken, Task<CodexControlledRouteResult>> execute,
        CancellationToken executionToken, CancellationToken sessionToken)
    {
        string cardId = "";
        var elapsed = Stopwatch.StartNew();
        string state = "已收到指令，正在准备；前一轮未结束时等待执行";
        try
        {
            await RetryAsync(async token => cardId = await createCard(Card(title, project, state, "blue", elapsed.Elapsed), token), sessionToken);
        }
        catch (Exception) when (!sessionToken.IsCancellationRequested)
        {
            await BestEffortTextAsync($"Task：{title}\n已收到指令，正在准备。完成后会自动将结果发送到本聊天。", "received", sessionToken);
        }

        using var pulses = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reporter = ReportAsync();
        CodexControlledRouteResult? result = null;
        string? error = null;
        bool inputRejected = false;
        try
        {
            result = await execute(() =>
            {
                Volatile.Write(ref state, "Codex 已接收，正在运行；完成后自动返回结果");
                running.TrySetResult();
            }, executionToken);
        }
        catch (CodexTaskBusyException) when (durableQueue) { throw; }
        catch (FeishuQueueInputCanceledException) when (durableQueue) { throw; }
        catch (OperationCanceledException) when (durableQueue && sessionToken.IsCancellationRequested) { throw; }
        catch (FeishuInputRejectedException exception)
        {
            inputRejected = true;
            error = string.IsNullOrWhiteSpace(exception.Message)
                ? "输入未通过检查，请修正后重新提交。" : exception.Message;
        }
        catch (OperationCanceledException)
        {
            error = "等待已超时或连接已取消，请在 Codex Desktop 确认任务状态；不会重复执行。";
        }
        catch (Exception exception)
        {
            error = exception is InvalidOperationException ? exception.Message : $"执行连接异常（{exception.GetType().Name}），请在 Codex Desktop 查看。";
        }
        finally
        {
            pulses.Cancel();
            await reporter;
        }

        bool success = result?.Status == "completed";
        bool pending = result?.Status == "pending";
        if (pending && durableQueue)
        {
            // The durable input's turn ID transfers ownership to the result worker.
            // A bridge reconnect is not a terminal Task result or a second chat reply.
            await UpdateAsync("任务仍在执行，桥接将继续跟踪并回传最终结果", "orange", sessionToken);
            return new(false, false, null, Pending: true);
        }
        bool stopped = result?.Status == "interrupted";
        string label = inputRejected ? "未执行" : success ? "已完成" : pending ? "等待已结束，执行未被停止" : stopped ? "已停止" : "未完成";
        bool errorNotice = result is null && error is not null;
        string deliveryContent = errorNotice ? "错误说明" : "结果";
        string text = result?.AgentMessageText ?? "";
        if (string.IsNullOrWhiteSpace(text))
            text = success ? "任务已完成，本轮未返回文字结果；请在 Codex Desktop 查看产物。"
                : error ?? (stopped ? "本轮任务已中断。" : "Codex 本轮执行失败，请在桌面端查看详情。");
        if (!success && !pending) error ??= text;
        string[] chunks = SplitUtf8(text).ToArray();
        FeishuResultPart[] parts = chunks.Select((chunk, index) => new FeishuResultPart(
            $"Task：{Short(title, 100)} · {label}{(chunks.Length == 1 ? "" : $"（{index + 1}/{chunks.Length}）")}\n\n{chunk}",
            $"result-{index}")).ToArray();
        bool persisted = false;
        bool delivered = true;
        try
        {
            if (prepareResults is not null)
            {
                // Local persistence must survive loss of the network session after completion.
                await prepareResults(parts, CancellationToken.None);
                persisted = true;
            }
            await UpdateAsync($"正在发送{deliveryContent}", success ? "blue" : "red", sessionToken);
            foreach (FeishuResultPart part in parts)
            {
                await RetryAsync(token => sendText(part.Text, part.Key, token), sessionToken);
            }
        }
        catch (Exception exception)
        {
            delivered = false;
            error = persisted
                ? $"{label}，{deliveryContent}已保存在本机，等待连接恢复后自动补发；任务不会重跑。"
                : errorNotice
                    ? $"{label}，但错误说明投递未确认（{exception.GetType().Name}）；任务不会重跑。"
                    : $"{label}，但结果投递未确认（{exception.GetType().Name}）；任务不会重跑，请在 Codex Desktop 查看结果。";
        }
        await UpdateAsync(delivered ? pending ? "等待已结束；状态说明已发送，最终结果待回传"
                : errorNotice ? $"{label}，错误说明已发送" : $"{label}，结果已返回飞书" : error!,
            pending ? "orange" : success && delivered ? "green" : stopped ? "grey" : "red", sessionToken);
        return new(success, delivered, error, pending);

        async Task ReportAsync()
        {
            try
            {
                Task signal = running.Task;
                while (true)
                {
                    await Task.WhenAny(signal, Task.Delay(pulseInterval ?? TimeSpan.FromSeconds(10), pulses.Token));
                    pulses.Token.ThrowIfCancellationRequested();
                    await UpdateAsync(Volatile.Read(ref state), "blue", pulses.Token);
                    if (signal.IsCompleted) signal = Task.Delay(Timeout.Infinite, pulses.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        async Task UpdateAsync(string status, string color, CancellationToken token)
        {
            if (cardId.Length == 0 || token.IsCancellationRequested) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await patchCard(cardId, Card(title, project, status, color, elapsed.Elapsed), timeout.Token);
            }
            catch { /* A failed card patch cannot suppress the final result. */ }
        }
    }

    private async Task RetryAsync(Func<CancellationToken, Task> send, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await send(timeout.Token);
                return;
            }
            catch when (attempt < 2 && !token.IsCancellationRequested)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2 * (attempt + 1)), token);
            }
        }
    }

    private async Task BestEffortTextAsync(string text, string purpose, CancellationToken token)
    {
        try { await RetryAsync(ct => sendText(text, purpose, ct), token); }
        catch { }
    }

    internal static IEnumerable<string> SplitUtf8(string text, int maxBytes = 3300)
    {
        if (maxBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var chunk = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                yield return chunk.ToString();
                chunk.Clear();
                bytes = 0;
            }
            chunk.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        if (chunk.Length > 0) yield return chunk.ToString();
    }

    internal static string Card(string title, string project, string state, string color, TimeSpan elapsed) =>
        JsonSerializer.Serialize(new
        {
            schema = "2.0", config = new { update_multi = true, enable_forward = false },
            header = new { title = new { tag = "plain_text", content = "Task：" + Short(title, 100) }, template = color },
            body = new { elements = new object[]
            {
                new { tag = "markdown", content = $"**项目**：{Escape(Short(project, 120))}\n**状态**：{Escape(Short(state, 450))}\n已用时：{(int)elapsed.TotalSeconds} 秒" },
            } },
        });

    private static string Short(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static string Escape(string value)
    {
        foreach (char character in "\\`*_{}[]()#+-.!|>")
            value = value.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        return value;
    }
}
