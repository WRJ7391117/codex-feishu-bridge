using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

internal static class TaskFeedbackRegressionTests
{
    internal static async Task RunAsync(Action<bool,string> assert)
    {
        string longText = string.Concat(Enumerable.Repeat("中文🙂\n完整结果，不得截断。", 600));
        string[] parts = FeishuTaskFeedback.SplitUtf8(longText).ToArray();
        assert(parts.Length > 2 && string.Concat(parts) == longText, "long multilingual results remain byte-for-byte complete");
        assert(parts.All(p => Encoding.UTF8.GetByteCount(p) <= 3300 && !p.Contains('\uFFFD')),
            "parts obey the sidecar byte limit without splitting surrogate pairs");
        var events = new List<string>();
        var delivered = new List<string>();
        int executions = 0;
        int resultAttempts = 0;
        var feedback = new FeishuTaskFeedback(
            (card, token) => { events.Add("received"); return Task.FromResult("owned-card"); },
            (id, card, token) =>
            {
                assert(id == "owned-card", "all progress updates target the original acknowledgement card");
                using JsonDocument document = JsonDocument.Parse(card);
                string status = document.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()!;
                events.Add(status);
                return Task.CompletedTask;
            },
            (text, key, token) =>
            {
                assert(Encoding.UTF8.GetByteCount(text) <= 4000, "headers plus result content fit the transport limit");
                if (key == "result-0" && ++resultAttempts == 1) throw new IOException("temporary send failure");
                delivered.Add(text);
                events.Add(key);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
        FeishuTaskFeedbackOutcome outcome = await feedback.RunAsync("真实任务", "项目",
            async (started, token) =>
            {
                assert(events[0] == "received", "acknowledgement precedes execution");
                executions++;
                started();
                await Task.Delay(40, token);
                return new("thread", "turn", "completed", longText);
            }, CancellationToken.None, CancellationToken.None);
        assert(executions == 1 && resultAttempts == 2 && delivered.Count == parts.Length,
            "a failed send retries the same result part without rerunning the Task or duplicating delivered parts");
        assert(outcome.TaskSucceeded && outcome.Delivered && outcome.Error is null, "success is recorded only after all parts are confirmed");
        assert(events.Any(e => e.Contains("Codex 已接收")) && events[^1].Contains("结果已返回飞书"),
            "the same card reaches running and delivered states without late running patches");
        assert(string.Concat(delivered.Select(t => t[(t.IndexOf("\n\n", StringComparison.Ordinal)+2)..])) == longText,
            "the entire original result reaches the bot chat in order");

        foreach (string scenario in new[] { "timeout", "interrupted", "failed", "empty", "delivery-failed", "card-failed", "pending" })
        {
            var texts = new List<string>();
            int runs = 0;
            var service = new FeishuTaskFeedback(
                (card, token) => scenario == "card-failed" ? throw new IOException() : Task.FromResult("card"),
                (id, card, token) => Task.CompletedTask,
                (text, key, token) =>
                {
                    assert(!token.IsCancellationRequested, "terminal feedback uses a fresh delivery token, not the expired execution token");
                    if (scenario == "delivery-failed") throw new IOException();
                    texts.Add(text);
                    return Task.CompletedTask;
                }, TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
            using var execution = new CancellationTokenSource();
            var actual = await service.RunAsync("task", "project", (started, token) =>
            {
                runs++;
                if (scenario == "timeout") { execution.Cancel(); throw new OperationCanceledException(token); }
                started();
                return Task.FromResult(new CodexControlledRouteResult("thread", "turn",
                    scenario is "failed" or "interrupted" or "pending" ? scenario : "completed", scenario == "empty" ? "" : "response"));
            }, execution.Token, CancellationToken.None);
            assert(runs == 1, "feedback failures must never rerun execution");
            if (scenario == "pending") assert(actual.Pending && actual.Error is null && texts[^1].Contains("执行未被停止"),
                "observation pending produces a status notice, not a false task failure");
            if (scenario == "delivery-failed")
                assert(actual.TaskSucceeded && !actual.Delivered && actual.Error!.Contains("投递未确认"), "delivery failure remains distinct from execution failure");
            else
                assert(actual.Delivered && texts.Count > 0, "completion, error, interruption and empty results all produce visible feedback");
            if (scenario == "card-failed")
                assert(texts[0].Contains("已收到指令") && texts[^1].Contains("已完成"), "card failures use a text receipt and still deliver the final result");
        }
        using var response = JsonDocument.Parse("{\"type\":\"command_result\",\"request_id\":\"r\",\"state\":\"succeeded\",\"message_id\":\"owned-card\"}");
        assert(FeishuSidecarProtocol.TryReadCommandResult(response.RootElement, out _, out var parsed) && parsed.MessageId == "owned-card",
            "the sidecar card id survives parsing for subsequent updates");

        await VerifyInputRejectionAsync(assert);

        DirectoryInfo directory = Directory.CreateTempSubdirectory("deepori-result-outbox-tests-");
        try
        {
            var envelope = new FeishuResultEnvelope("generation", "owner", "project",
                [new("first part", "stable-key-0"), new("second part", "stable-key-1")]);
            var outbox = new FeishuTaskResultOutbox(directory.FullName);
            await outbox.SaveAsync("source-message", envelope, CancellationToken.None);
            byte[] persisted = File.ReadAllBytes(Directory.GetFiles(directory.FullName, "*.bin").Single());
            assert(!Encoding.UTF8.GetString(persisted).Contains("first part"), "pending results are encrypted at rest with Windows user protection");
            var sentKeys = new List<string>();
            Task Send(FeishuResultEnvelope job, FeishuResultPart part, CancellationToken token)
            { sentKeys.Add(part.Key); return Task.CompletedTask; }
            await outbox.SendPartAsync("source-message", "stable-key-0", _ => true, Send, CancellationToken.None);
            var restarted = new FeishuTaskResultOutbox(directory.FullName);
            await restarted.FlushAsync(_ => false, Send, CancellationToken.None);
            assert(sentKeys.SequenceEqual(["stable-key-0"]), "revoked identity or project access cannot receive an old pending result");
            await restarted.FlushAsync(_ => true, Send, CancellationToken.None);
            assert(sentKeys.SequenceEqual(["stable-key-0", "stable-key-1"]), "restart recovers only unsent parts and preserves their original idempotency keys");
            await restarted.SendPartAsync("source-message", "stable-key-1", _ => true, Send, CancellationToken.None);
            await restarted.FlushAsync(_ => true, Send, CancellationToken.None);
            assert(sentKeys.Count == 2, "acknowledged results stay deduplicated across restart and concurrent retry");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifyInputRejectionAsync(Action<bool, string> assert)
    {
        foreach (string scenario in new[] { "rejected", "rejected-retry", "rejected-unconfirmed", "rejected-persisted",
                     "rejected-empty", "unknown-before-callback", "unknown-after-callback", "invalid-operation", "cancelled" })
        {
            bool rejected = scenario.StartsWith("rejected", StringComparison.Ordinal);
            bool unconfirmed = scenario is "rejected-unconfirmed" or "rejected-persisted";
            var cards = new List<string>();
            var texts = new List<string>();
            var keys = new List<string>();
            FeishuResultPart[]? saved = null;
            int runs = 0;
            int submissions = 0;
            int callbacks = 0;
            var feedback = new FeishuTaskFeedback(
                (card, token) => { Capture(card); return Task.FromResult("card"); },
                (id, card, token) => { Capture(card); return Task.CompletedTask; },
                (text, key, token) =>
                {
                    texts.Add(text);
                    keys.Add(key);
                    if (unconfirmed || (scenario == "rejected-retry" && keys.Count == 1)) throw new IOException();
                    return Task.CompletedTask;
                }, TimeSpan.FromHours(1), TimeSpan.Zero,
                prepareResults: scenario == "rejected-persisted"
                    ? (parts, token) => { saved = parts; return Task.CompletedTask; } : null);

            var outcome = await feedback.RunAsync("input", "project", async (started, token) =>
            {
                runs++;
                if (rejected)
                    throw new FeishuInputRejectedException(scenario == "rejected-empty" ? " " : "图片解析失败，请重新上传。");
                // Submission can succeed before the transport fails to report a running callback.
                submissions++;
                if (scenario == "unknown-after-callback")
                {
                    callbacks++;
                    started();
                    await Task.Delay(20, token);
                }
                if (scenario == "invalid-operation") throw new InvalidOperationException("连接响应解析失败。");
                if (scenario == "cancelled") throw new OperationCanceledException(token);
                throw new IOException("private transport detail");
            }, CancellationToken.None, CancellationToken.None);

            assert(runs == 1 && keys.All(key => key == "result-0"),
                $"{scenario}: notification retries keep one execution and stable delivery keys");
            assert(keys.Count == (unconfirmed ? 3 : scenario == "rejected-retry" ? 2 : 1),
                $"{scenario}: only delivery is retried");
            assert(!outcome.TaskSucceeded && !outcome.Pending && outcome.Delivered == !unconfirmed && outcome.Error is not null,
                $"{scenario}: rejection/error and delivery outcomes remain distinct");
            assert(cards.All(card => !card.Contains("结果已返回飞书")) && texts.All(text => !text.Contains("结果已返回飞书")),
                $"{scenario}: error delivery never claims a task result was returned");
            if (rejected)
            {
                assert(submissions == 0 && callbacks == 0 && cards.All(card => !card.Contains("Codex 已接收") && !card.Contains("正在运行")),
                    $"{scenario}: rejected input never emits an execution receipt or running state");
                assert(texts.All(text => text.Contains("未执行") && text.Contains(scenario == "rejected-empty" ? "输入未通过检查" : "图片解析失败")),
                    $"{scenario}: rejection explanation is delivered with the nonexecution label");
                assert(unconfirmed ? !cards[^1].Contains("错误说明已发送") : cards[^1].Contains("未执行，错误说明已发送"),
                    $"{scenario}: the rejection card claims delivery only after confirmation");
                if (scenario == "rejected-unconfirmed")
                    assert(cards[^1].Contains("错误说明投递未确认") && !cards[^1].Contains("查看结果"),
                        "unconfirmed input rejection does not point to a nonexistent Codex result");
                if (scenario == "rejected-persisted")
                    assert(saved is { Length: 1 } && saved[0].Text == texts[0] && saved[0].Key == keys[0]
                        && cards[^1].Contains("错误说明已保存在本机") && cards[^1].Contains("自动补发"),
                        "persisted rejection retries the original explanation, without claiming execution or delivery");
            }
            else
            {
                assert(submissions == 1 && cards.All(card => !card.Contains("未执行")) && texts.All(text => !text.Contains("未执行")),
                    $"{scenario}: missing running callback never proves nonexecution");
                assert(cards[^1].Contains("未完成，错误说明已发送"),
                    $"{scenario}: unknown execution status is preserved after error delivery");
                assert(texts.All(text => !text.Contains("private transport detail")),
                    $"{scenario}: unexpected exception details are not exposed");
                if (scenario == "cancelled")
                    assert(texts[0].Contains("确认任务状态") && texts[0].Contains("不会重复执行"),
                        "cancelled observation does not claim the task stopped or trigger another execution");
            }

            void Capture(string card)
            {
                using var document = JsonDocument.Parse(card);
                cards.Add(document.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()!);
            }
        }
    }
}
