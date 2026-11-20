using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

internal static class TaskInputQueueRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("deepori-queue-tests-");
        try
        {
            var executionGate = new TaskExecutionGate(2);
            using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using IDisposable taskA = await executionGate.EnterAsync("A", gateTimeout.Token);
            Task<IDisposable> nextA = executionGate.EnterAsync("A", gateTimeout.Token);
            using IDisposable taskB = await executionGate.EnterAsync("B", gateTimeout.Token);
            assert(!nextA.IsCompleted, "same Task remains serialized while an independent Task uses the second slot");
            Task<IDisposable> taskC = executionGate.EnterAsync("C", gateTimeout.Token);
            assert(!taskC.IsCompleted, "a third independent Task waits for the Mac-equivalent two-task limit");
            taskB.Dispose();
            using IDisposable releasedC = await taskC.WaitAsync(gateTimeout.Token);
            assert(!nextA.IsCompleted, "a same-Task waiter does not block capacity released to another Task");
            releasedC.Dispose();
            taskA.Dispose();
            using IDisposable releasedA = await nextA.WaitAsync(gateTimeout.Token);
            using var canceledWait = new CancellationTokenSource();
            Task<IDisposable> blockedA = executionGate.EnterAsync("A", canceledWait.Token);
            canceledWait.Cancel();
            try { await blockedA; assert(false, "canceled waiter must not acquire a lease"); }
            catch (OperationCanceledException) { assert(true, "canceling a same-Task waiter safely releases its reference"); }
            using IDisposable anotherB = await executionGate.EnterAsync("B", gateTimeout.Token);

            string storePath = Path.Combine(root.FullName, "queue");
            var queue = new FeishuTaskInputQueue(storePath);
            FeishuQueuedInput Input(string message, string task = "task-a", string owner = "owner") => new(
                FeishuTaskInputQueue.Key("generation", owner, message), message, owner, "generation",
                new(task, root.FullName, "project-a"), new(task, "任务", "", root.FullName, "", "idle", []),
                "PRIVATE_PROMPT_截图+文字", "{\"resources\":[{\"kind\":\"image\",\"key\":\"img_fixture\"}]}", DateTimeOffset.UtcNow);
            FeishuQueuedInput first = Input("first"), second = Input("second");
            assert(queue.Enqueue(first) == (true, 1) && queue.Enqueue(second) == (true, 2), "queue acceptance preserves arrival order and reports positions");
            assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(storePath, "queue.bin"))).Contains(first.Prompt),
                "queue contents are DPAPI protected on disk, not plaintext prompts or resource keys");
            var restarted = new FeishuTaskInputQueue(storePath);
            assert(restarted.Enqueue(first) == (false, 0), "a replayed Feishu message is deduplicated after bridge restart");
            assert(restarted.Pending().Select(e => e.MessageId).SequenceEqual(["first", "second"]), "queued text and attachments survive restart in FIFO order");
            assert(restarted.Pending()[0].ResourceJson == first.ResourceJson, "restart retains the same resource references without guessing a new message");
            assert(FeishuTaskInputQueue.Heads(restarted.Pending()).Single().Id == first.Id, "the second input cannot overtake the same Task's head");
            assert(queue.Transition(first.Id, "queued", "submitting"), "one dispatcher durably claims a submission");
            assert(!restarted.Transition(first.Id, "queued", "submitting"), "another dispatcher cannot submit a claimed message twice");
            assert(FeishuTaskInputQueue.Heads(restarted.Pending()).Single().Phase == "submitting", "unknown submission blocks later messages instead of replaying");
            assert(!queue.Transition(first.Id, "queued", "finished"), "canceling queued work cannot cancel an already submitted Task");
            queue.RequeueBusy(first.Id);
            FeishuQueuedInput retry = restarted.Pending()[0];
            assert(retry.Phase == "queued" && retry.AvailableAt > DateTimeOffset.UtcNow.AddSeconds(10), "explicit busy non-acceptance preserves position with Mac's 15-second backoff");
            assert(queue.Transition(first.Id, "queued", "submitting") && queue.Transition(first.Id, "submitting", "running", "turn-first"),
                "confirmed start records the exact turn ID before waiting for completion");
            assert(restarted.Pending()[0].TurnId == "turn-first" && restarted.Pending()[0].Phase == "running", "restart resumes result observation, not execution");
            assert(queue.Transition(first.Id, "running", "finished"), "terminal result persistence releases the next queue entry");
            assert(FeishuTaskInputQueue.Heads(queue.Pending()).Single().Id == second.Id, "the next message becomes runnable only after the previous head finishes");
            assert(!restarted.Enqueue(first).Added, "a completed message receipt still prevents duplicate execution");
            assert(queue.Transition(second.Id, "queued", "finished") && !queue.Transition(second.Id, "queued", "submitting"),
                "cancellation wins atomically against the submit claim");
            var changedSelection = new CurrentTaskSelection("task-b", root.FullName, "project-b");
            FeishuQueuedInput third = Input("third");
            queue.Enqueue(third);
            assert(restarted.Pending()[0].Selection == third.Selection && restarted.Pending()[0].Selection != changedSelection,
                "changing the selected Task does not retarget an accepted input");
            queue.Enqueue(Input("fourth", "task-b"));
            assert(FeishuTaskInputQueue.Heads(queue.Pending()).Count() == 2, "different Task queues have independent heads");
            assert(FeishuTaskInputQueue.Key("a", "bc", "d") != FeishuTaskInputQueue.Key("ab", "c", "d"), "identity boundaries cannot collide by concatenation");
            assert(queue.Enqueue(Input("third", owner: "new-owner")).Added, "deduplication is scoped to the original Bot generation and owner");
            var retiredStore = new FeishuTaskInputQueue(Path.Combine(root.FullName, "retired"));
            var retired = Input("old-owner", owner: "revoked-owner");
            var uncertainOld = Input("uncertain-old", "task-uncertain", owner: "revoked-owner");
            retiredStore.Enqueue(retired);
            retiredStore.Enqueue(uncertainOld);
            retiredStore.Transition(uncertainOld.Id, "queued", "submitting");
            assert(retiredStore.RetireQueued(e => e.Sender == "revoked-owner") == 1,
                "authorization revocation retires queued work but preserves uncertain submission evidence");
            assert(retiredStore.Pending().Single().Phase == "submitting", "retirement never replays or hides an uncertain start");
            assert(!retiredStore.Enqueue(retired).Added, "retired input retains deduplication receipt");
            assert(retiredStore.Transition(uncertainOld.Id, "submitting", "finished")
                && !retiredStore.Transition(uncertainOld.Id, "submitting", "finished"),
                "manual resolution is one-shot and cannot repeat a start");

            var limits = new FeishuTaskInputQueue(Path.Combine(root.FullName, "limits"));
            for (int t = 0; t < 5; t++)
                for (int m = 0; m < 10; m++) limits.Enqueue(Input($"{t}-{m}", $"task-{t}"));
            try { limits.Enqueue(Input("overflow", "new-task")); assert(false, "global capacity must reject overflow"); }
            catch (InvalidOperationException) { assert(limits.Pending().Length == 50, "Mac global capacity is 50 pending inputs, with no silent drop"); }
            var perTask = new FeishuTaskInputQueue(Path.Combine(root.FullName, "per-task"));
            for (int m = 0; m < 10; m++) perTask.Enqueue(Input($"item-{m}"));
            try { perTask.Enqueue(Input("overflow")); assert(false, "per-Task capacity must reject overflow"); }
            catch (InvalidOperationException) { assert(perTask.Pending().Length == 10, "Mac per-Task capacity is 10 queued inputs"); }
            string corrupt = Path.Combine(root.FullName, "corrupt");
            Directory.CreateDirectory(corrupt);
            await File.WriteAllBytesAsync(Path.Combine(corrupt, "queue.bin"), [1, 2, 3]);
            try { new FeishuTaskInputQueue(corrupt).Enqueue(Input("unsafe")); assert(false, "corrupt queue must not authorize a start"); }
            catch (System.Security.Cryptography.CryptographicException) { assert(true, "corrupt encrypted queue fails closed without erasing receipts"); }

            string log = Path.Combine(root.FullName, "rollout-fixture-task.jsonl");
            await File.WriteAllTextAsync(log, Event("task_started", "first-turn") + Event("task_complete", "first-turn", "first reply")
                + Event("task_started", "second-turn") + Event("turn_aborted", "second-turn"));
            var reader = new CodexRolloutTaskStateReader(root.FullName);
            assert(reader.ReadTerminalResult("fixture-task", "first-turn")?.AgentMessageText == "first reply", "recovery finds the original turn even after a later turn");
            assert(reader.ReadTerminalResult("fixture-task", "second-turn")?.Status == "interrupted", "recovery distinguishes canceled execution from successful completion");
            assert(reader.ReadTerminalResult("fixture-task", "missing") is null, "another turn's completion cannot release this queue head");

            foreach (string scenario in new[] { "busy", "success", "lost", "rejected" })
            {
                FeishuQueuedInput request = Input("ipc-" + scenario, "fixture-task");
                queue.Enqueue(request);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string pipeName = "queue-regression-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                int submissions = 0;
                Task remote = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync(deadline.Token);
                    using JsonDocument initialize = await CodexDesktopTurnClient.ReadFrameAsync(server, deadline.Token);
                    await Reply(server, initialize.RootElement, new { clientId = "fixture" }, deadline.Token);
                    using JsonDocument start = await CodexDesktopTurnClient.ReadFrameAsync(server, deadline.Token);
                    submissions++;
                    assert(new FeishuTaskInputQueue(storePath).Pending().Single(e => e.Id == request.Id).Phase == "submitting", "durable claim is written BEFORE bytes reach Desktop");
                    if (scenario == "lost") { server.Disconnect(); return; }
                    if (scenario is "busy" or "rejected")
                    {
                        await CodexDesktopTurnClient.WriteFrameAsync(server, new { type = "response", requestId = start.RootElement.GetProperty("requestId").GetString(), resultType = "error", error = scenario == "busy" ? "active turn" : "permission denied" }, deadline.Token);
                        return;
                    }
                    await Reply(server, start.RootElement, new { result = new { turn = new { id = "queue-turn" } } }, deadline.Token);
                    await File.AppendAllTextAsync(log, Event("task_complete", "queue-turn", "queued reply"));
                });
                try
                {
                    var result = await new CodexDesktopTurnClient(pipeName).TryRunAsync("fixture-task", request.Prompt, root.FullName,
                        id => queue.Transition(request.Id, "submitting", "running", id), () => true, deadline.Token, reader,
                        onSubmitting: () => { if (!queue.Transition(request.Id, "queued", "submitting")) throw new InvalidOperationException("already claimed"); },
                        onNotAccepted: () => queue.Transition(request.Id, "submitting", "queued"));
                    assert(scenario == "success" && result?.AgentMessageText == "queued reply", "accepted queued input yields its own completed result");
                }
                catch (CodexTaskBusyException) { assert(scenario == "busy", "only explicit busy rejection permits requeue"); queue.RequeueBusy(request.Id); }
                catch (InvalidOperationException) { assert(scenario is "lost" or "rejected", "only explicit rejection clears the uncertain claim"); }
                await remote;
                assert(submissions == 1, "queue transport never sends the same input twice in an attempt");
                string expected = scenario is "busy" or "rejected" ? "queued" : scenario == "success" ? "running" : "submitting";
                assert(restarted.Pending().Single(e => e.Id == request.Id).Phase == expected, "restart retains the correct busy/confirmed/uncertain lifecycle");
            }

            int resultSends = 0, persisted = 0;
            var feedback = new FeishuTaskFeedback((_, _) => Task.FromResult("card"), (_, _, _) => Task.CompletedTask,
                (_, _, _) => { resultSends++; return Task.CompletedTask; }, prepareResults: (_, _) => { persisted++; return Task.CompletedTask; }, durableQueue: true);
            var pending = await feedback.RunAsync("Task", "Project", (_, _) => Task.FromResult(new CodexControlledRouteResult("task", "turn", "pending", "old disconnect notice")), CancellationToken.None, CancellationToken.None);
            assert(pending.Pending && resultSends == 0 && persisted == 0, "reconnect does not send or persist a spurious terminal result for durable queued work");
            try { await feedback.RunAsync("Task", "Project", (_, _) => throw new CodexTaskBusyException(), CancellationToken.None, CancellationToken.None); assert(false, "busy must return to queue"); }
            catch (CodexTaskBusyException) { assert(resultSends == 0, "busy returns to the queue without a failure chat message"); }
            try { await feedback.RunAsync("Task", "Project", (_, _) => throw new FeishuQueueInputCanceledException(), CancellationToken.None, CancellationToken.None); assert(false, "canceled queue claim must not become an uncertain-start warning"); }
            catch (FeishuQueueInputCanceledException) { assert(resultSends == 0, "canceled queue claim sends no misleading timeout or failure result"); }

            // Exercise the actual update guard, but never invoke the updater or
            // touch the running Bridge. Only this temporary fixture is observed.
            string guardLog = Path.Combine(root.FullName, "guard.jsonl");
            await File.WriteAllTextAsync(guardLog, Event("task_started", "guard-turn"));
            System.Diagnostics.Process Guard(int timeout)
            {
                var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(Directory.GetCurrentDirectory(), "windows", "scripts", "Wait-TaskIdle.ps1"),
                    "-RolloutPath", guardLog, "-TurnId", "guard-turn", "-QuietSeconds", "1", "-MaxWaitSeconds", timeout.ToString() }) start.ArgumentList.Add(argument);
                return System.Diagnostics.Process.Start(start)!;
            }
            using (var guard = Guard(12))
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Task<string> output = guard.StandardOutput.ReadToEndAsync(limit.Token);
                Task<string> errors = guard.StandardError.ReadToEndAsync(limit.Token);
                await Task.Delay(700, limit.Token);
                assert(!guard.HasExited, "updater guard does not interrupt a running Task");
                await File.AppendAllTextAsync(guardLog, Event("task_complete", "unrelated-turn"));
                await Task.Delay(700, limit.Token);
                assert(!guard.HasExited, "unrelated completion cannot authorize an update");
                string partial = Event("task_complete", "guard-turn");
                await File.AppendAllTextAsync(guardLog, partial[..^2]);
                await Task.Delay(700, limit.Token);
                assert(!guard.HasExited, "a partial JSONL line cannot authorize an update");
                await File.AppendAllTextAsync(guardLog, partial[^2..] + Event("task_started", "next-turn"));
                await Task.Delay(1500, limit.Token);
                assert(!guard.HasExited, "a follow-up Task turn delays deployment instead of being interrupted");
                await File.AppendAllTextAsync(guardLog, Event("task_complete", "next-turn"));
                await guard.WaitForExitAsync(limit.Token);
                assert(guard.ExitCode == 0 && (await output).Contains("Task stayed idle") && string.IsNullOrWhiteSpace(await errors),
                    "only a complete target turn and a stable idle window permit update");
            }
            await File.WriteAllTextAsync(guardLog, Event("task_started", "guard-turn"));
            using (var guard = Guard(2))
            {
                Task<string> errors = guard.StandardError.ReadToEndAsync();
                await guard.WaitForExitAsync();
                assert(guard.ExitCode != 0 && (await errors).Contains("left running and unchanged"), "guard deadline cancels deployment, not the user's Task");
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static string Event(string type, string turnId, string text = "") => JsonSerializer.Serialize(new { type = "event_msg", payload = new { type, turn_id = turnId, last_agent_message = text } }) + "\n";
    private static Task Reply(Stream stream, JsonElement request, object result, CancellationToken token) =>
        CodexDesktopTurnClient.WriteFrameAsync(stream, new { type = "response", requestId = request.GetProperty("requestId").GetString(), resultType = "success", result }, token);
}
