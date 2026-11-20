using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

internal static class DesktopRoutingRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var sent = new PromLightActivitySnapshot(PromLightStatus.Running, 65, now);
        assert(!PromLightProtocol.NeedsRefresh(sent, PromLightStatus.Running, now.AddSeconds(2)),
            "lamp does not continuously rewrite a just-confirmed status");
        assert(PromLightProtocol.NeedsRefresh(sent, PromLightStatus.Running, now.AddSeconds(15)),
            "unchanged Task status is reasserted after disconnect/reconnect within the refresh interval");
        assert(PromLightProtocol.NeedsRefresh(sent, PromLightStatus.Idle, now)
            && PromLightProtocol.NeedsRefresh(PromLightActivitySnapshot.Initial, PromLightStatus.Running, now),
            "state changes and lost device handles always need delivery");
        var error = PromLightTransientSignal.Create(PromLightStatus.Error, now);
        assert(PromLightTransientSignal.Effective(PromLightStatus.Idle, false, error, now) == PromLightStatus.Error,
            "an unbound lamp can show a brief delivery error");
        assert(PromLightTransientSignal.Effective(PromLightStatus.Idle, false, error, now.AddSeconds(8)) == PromLightStatus.Idle,
            "delivery errors expire without another successful message");
        assert(PromLightTransientSignal.Effective(PromLightStatus.Running, true, error, now) == PromLightStatus.Running,
            "a linked Task's running state is not masked by a bridge delivery error");
        assert(PromLightTransientSignal.Effective(PromLightStatus.Idle, true, error, now) == PromLightStatus.Idle,
            "linking an idle Task immediately suppresses a prior transport error");
        assert(PromLightTransientSignal.Effective(PromLightStatus.Error, true, null, now) == PromLightStatus.Error,
            "real associated Task failures still show red");
        assert(FeishuCurrentTaskCardBuilder.ProjectStatus("active", false, false) == "运行中",
            "the app-server active status must not be displayed as idle");

        DirectoryInfo root = Directory.CreateTempSubdirectory("deepori-desktop-regression-");
        try
        {
            string sessionRoot = Path.Combine(root.FullName, "sessions");
            string archivedRoot = Path.Combine(root.FullName, "archived_sessions");
            Directory.CreateDirectory(sessionRoot);
            Directory.CreateDirectory(archivedRoot);
            string archivedPath = Path.Combine(archivedRoot, "rollout-archived-task.jsonl");
            await File.WriteAllTextAsync(archivedPath, Event("task_started", "archived-turn")
                + Event("turn_aborted", "archived-turn"));
            var archivedReader = new CodexRolloutTaskStateReader(sessionRoot);
            assert(archivedReader.ResolvePath("archived-task") is null
                && archivedReader.ReadTerminalResult("archived-task", "archived-turn")?.Status == "interrupted",
                "archive-only exact terminal recovery finishes accepted queue work without reviving a runnable Task");
            assert(archivedReader.ReadTerminalResult("archived-task", "another-turn") is null,
                "archive recovery cannot substitute a different turn");
            const string threadId = "fixture-task";
            string path = Path.Combine(root.FullName, $"rollout-{threadId}.jsonl");
            var reader = new CodexRolloutTaskStateReader(root.FullName);
            await File.WriteAllTextAsync(path, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"用户原始指令\"}}\n");
            assert(reader.ReadLatestReply(threadId) == "", "a user's prompt must never appear as the latest assistant reply");
            await File.AppendAllTextAsync(path, Event("task_complete", "reply-turn", "助手的实际结果"));
            assert(reader.ReadLatestReply(threadId) == "助手的实际结果", "the current Task card shows only a real completed assistant reply");
            await File.WriteAllTextAsync(path, Event("task_started", "old-turn")
                + Event("turn_aborted", "old-turn"));
            assert(reader.ReadLatestStatus(threadId) == PromLightStatus.Idle,
                "an interrupted historical turn must not make a newly associated lamp flash red");
            await File.AppendAllTextAsync(path, Event("task_started", "failed-turn")
                + Event("task_failed", "failed-turn"));
            assert(reader.ReadLatestStatus(threadId) == PromLightStatus.Error,
                "a real failed turn remains distinct from cancellation");
            await File.AppendAllTextAsync(path, Event("task_started", "new-turn"));
            assert(reader.ReadLatestStatus(threadId) == PromLightStatus.Running,
                "starting another turn replaces the previous failed state");
            await File.AppendAllTextAsync(path, Event("task_complete", "new-turn"));
            assert(reader.ReadLatestStatus(threadId) == PromLightStatus.Idle,
                "completing a turn clears the running state");

            string resumed = Path.Combine(root.FullName, $"rollout-{threadId}_{Guid.NewGuid()}.jsonl");
            await File.WriteAllTextAsync(resumed,
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = threadId } }) + "\n"
                + Event("task_complete", "resumed-turn", "恢复会话后的新结果"));
            File.SetLastWriteTimeUtc(resumed, DateTime.UtcNow.AddSeconds(2));
            assert(reader.ResolvePath(threadId) == resumed
                && reader.ReadLatestReply(threadId) == "恢复会话后的新结果",
                "resumed canonical Task files supersede stale original rollouts even after a cached lookup");
            string wrong = Path.Combine(root.FullName, $"rollout-{threadId}_{Guid.NewGuid()}.jsonl");
            await File.WriteAllTextAsync(wrong, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "another-task" } }) + "\n");
            File.SetLastWriteTimeUtc(wrong, DateTime.UtcNow.AddSeconds(5));
            assert(reader.ResolvePath(threadId) == resumed, "a filename alone cannot associate another Task's resumed session");
            File.Delete(resumed);
            File.Delete(wrong);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            long offset = new FileInfo(path).Length;
            Task<CodexControlledRouteResult> pending = CodexDesktopTurnClient.WaitForCompletionAsync(
                path, offset, threadId, "expected-turn", timeout.Token);
            await File.AppendAllTextAsync(path, Event("task_complete", "unrelated-turn", "wrong reply"));
            string complete = Event("task_complete", "expected-turn", "中文完整回复");
            byte[] bytes = Encoding.UTF8.GetBytes(complete);
            await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                await append.WriteAsync(bytes.AsMemory(0, bytes.Length - 2));
                await append.FlushAsync();
                await Task.Delay(300);
                assert(!pending.IsCompleted, "partial JSONL and another turn cannot complete this message");
                await append.WriteAsync(bytes.AsMemory(bytes.Length - 2));
                await append.FlushAsync();
            }
            CodexControlledRouteResult terminal = await pending;
            assert(terminal.TurnId == "expected-turn" && terminal.AgentMessageText == "中文完整回复",
                "only the exact acknowledged turn supplies the Feishu reply");

            foreach (string scenario in new[] { "success", "no-client-found", "busy", "lost", "missing-id", "revoked", "timeout" })
            {
                using var scenarioCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                string documentPath = Path.Combine(root.FullName, "attachment.txt");
                await File.WriteAllTextAsync(documentPath, "actual attachment");
                CodexInputAttachment[] inputAttachments = scenario == "success"
                    ? [new("file", documentPath, "中文附件.txt")] : [];
                string pipeName = "deepori-test-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                int sends = 0;
                Task serverTask = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    using JsonDocument initialize = await CodexDesktopTurnClient.ReadFrameAsync(server, timeout.Token);
                    await Reply(server, initialize.RootElement, new { clientId = "fixture-client" }, timeout.Token);
                    if (scenario == "revoked") return;
                    using JsonDocument start = await CodexDesktopTurnClient.ReadFrameAsync(server, timeout.Token);
                    JsonElement request = start.RootElement;
                    sends++;
                    assert(request.GetProperty("method").GetString() == "thread-follower-start-turn"
                        && request.GetProperty("version").GetInt32() == 2,
                        "Desktop routing uses the follower protocol, not thread/resume");
                    JsonElement parameters = request.GetProperty("params");
                    if (scenario == "success")
                    {
                        JsonElement startBody = parameters.GetProperty("turnStart");
                        JsonElement inputs = startBody.GetProperty("request").GetProperty("input");
                        assert(inputs.GetArrayLength() == 2 && inputs[1].GetProperty("type").GetString() == "mention"
                            && inputs[1].GetProperty("path").GetString() == documentPath,
                            "the real named-pipe start frame contains the downloaded file, not just its name");
                        assert(startBody.GetProperty("context").GetProperty("attachments")[0].GetProperty("fsPath").GetString() == documentPath,
                            "Desktop context preserves the same attachment filesystem path");
                    }
                    assert(parameters.GetProperty("conversationId").GetString() == threadId
                        && parameters.GetProperty("turnStart").GetProperty("request").GetProperty("threadId").GetString() == threadId
                        && parameters.GetProperty("turnStart").GetProperty("context").GetProperty("inheritThreadSettings").GetBoolean(),
                        "the selected thread and inherited Desktop settings are preserved");
                    if (scenario == "lost") { server.Disconnect(); return; }
                    if (scenario is "busy" or "no-client-found")
                    {
                        await CodexDesktopTurnClient.WriteFrameAsync(server, new
                        {
                            type = "response", requestId = request.GetProperty("requestId").GetString(),
                            resultType = "error", error = scenario == "busy" ? "active turn" : scenario,
                        }, timeout.Token);
                        return;
                    }
                    if (scenario == "missing-id")
                    {
                        await Reply(server, request, new { result = new { turn = new { } } }, timeout.Token);
                        return;
                    }
                    if (scenario == "timeout")
                    {
                        await Reply(server, request, new { result = new { turn = new { id = "long-running-turn" } } }, timeout.Token);
                        // Give the client its ACK before expiring only its observation.
                        await Task.Delay(100, timeout.Token);
                        scenarioCancellation.Cancel();
                        try
                        {
                            using var unexpected = await CodexDesktopTurnClient.ReadFrameAsync(server, timeout.Token);
                            assert(false, "observation timeout must not send a user-stop/interrupt request");
                        }
                        catch (EndOfStreamException) { assert(true, "observation timeout closes the observer without interrupting Desktop"); }
                        return;
                    }
                    // Exercise discovery and unrelated-response handling while awaiting ACK.
                    await CodexDesktopTurnClient.WriteFrameAsync(server, new
                    {
                        type = "client-discovery-request", requestId = "discovery",
                    }, timeout.Token);
                    using JsonDocument discovery = await CodexDesktopTurnClient.ReadFrameAsync(server, timeout.Token);
                    assert(!discovery.RootElement.GetProperty("response").GetProperty("canHandle").GetBoolean(),
                        "Bridge must not advertise itself as the Desktop writer");
                    await CodexDesktopTurnClient.WriteFrameAsync(server, new
                    {
                        type = "response", requestId = "unrelated", resultType = "success", result = new { },
                    }, timeout.Token);
                    // Completion can precede the acknowledgement on a very fast turn.
                    await File.AppendAllTextAsync(path, Event("task_complete", "desktop-turn", "Desktop reply"));
                    await Reply(server, request, new { result = new { turn = new { id = "desktop-turn" } } }, timeout.Token);
                });
                string? startedId = null;
                CodexControlledRouteResult? result = null;
                InvalidOperationException? failure = null;
                try
                {
                    result = await new CodexDesktopTurnClient(pipeName).TryRunAsync(
                        threadId, "fixture prompt", root.FullName, id => startedId = id,
                        () => scenario != "revoked", scenarioCancellation.Token, reader, inputAttachments);
                }
                catch (InvalidOperationException exception) { failure = exception; }
                await serverTask;
                assert(sends == (scenario == "revoked" ? 0 : 1), "the prompt is submitted at most once");
                if (scenario == "success")
                    assert(failure is null && startedId == "desktop-turn" && result?.AgentMessageText == "Desktop reply",
                        "an existing Desktop-owned Task returns its reply without acquiring another writer");
                else if (scenario == "no-client-found")
                    assert(failure is null && result is null, "explicit non-delivery permits app-server fallback");
                else if (scenario == "timeout")
                    assert(failure is null && result?.Status == "pending" && result.TurnId == "long-running-turn",
                        "deadline is an observation-pending state, not a completed/failed/cancelled Task");
                else
                    assert(failure is not null && startedId is null,
                        "busy, unconfirmed, or revoked requests must not return a retryable fallback result");
            }
        }
        finally { root.Delete(recursive: true); }
    }

    internal static async Task ProbeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, timeout.Token);
        JsonElement response = await CodexDesktopTurnClient.RequestAsync(pipe, "initializing-client", "initialize", 0,
            new { clientType = "feishu-bridge" }, timeout.Token);
        if (response.GetProperty("resultType").GetString() != "success"
            || string.IsNullOrEmpty(response.GetProperty("result").GetProperty("clientId").GetString()))
            throw new InvalidOperationException("Desktop IPC read-only handshake failed");
        Console.WriteLine("PASS: live Desktop named-pipe handshake; no prompt submitted");
    }

    private static string Event(string type, string turnId, string text = "") =>
        JsonSerializer.Serialize(new { type = "event_msg", payload = new { type, turn_id = turnId, last_agent_message = text } }) + "\n";

    private static Task Reply(Stream stream, JsonElement request, object result, CancellationToken token) =>
        CodexDesktopTurnClient.WriteFrameAsync(stream, new
        {
            type = "response", requestId = request.GetProperty("requestId").GetString(), resultType = "success", result,
        }, token);
}
