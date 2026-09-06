using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

internal static class DesktopSyncRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("deepori-desktop-sync-tests-");
        try
        {
            const string threadId = "desktop-sync-task";
            string rollout = Path.Combine(root.FullName, $"rollout-{threadId}.jsonl");
            var reader = new CodexRolloutTaskStateReader(root.FullName);
            assert(reader.ReadDesktopSyncSnapshot(threadId) == CodexDesktopSyncSnapshot.None,
                "a Task without a rollout reports no Desktop result");

            await File.WriteAllTextAsync(rollout,
                Event("task_started", "completed-turn")
                + Event("task_complete", "completed-turn", "桌面已有的完整结果"));
            CodexDesktopSyncSnapshot completed = reader.ReadDesktopSyncSnapshot(threadId);
            assert(completed.Status == "completed" && completed.TurnId == "completed-turn"
                && completed.AgentMessageText == "桌面已有的完整结果",
                "Desktop handoff reads the latest completed result instead of trusting thread/list status");

            await File.AppendAllTextAsync(rollout, Event("task_started", "running-turn"));
            CodexDesktopSyncSnapshot running = reader.ReadDesktopSyncSnapshot(threadId);
            assert(running.Status == "running" && running.TurnId == "running-turn",
                "Desktop handoff captures the exact running turn for restart-safe tracking");
            await File.AppendAllTextAsync(rollout,
                Event("task_complete", "running-turn", "运行完成后的结果"));
            assert(reader.ReadDesktopSyncSnapshot(threadId).AgentMessageText == "运行完成后的结果",
                "a tracked running turn becomes an immediately deliverable completion");

            await File.AppendAllTextAsync(rollout,
                Event("task_started", "failed-turn") + Event("task_failed", "failed-turn"));
            assert(reader.ReadDesktopSyncSnapshot(threadId).Status == "failed",
                "failed Desktop turns remain visible terminal states");
            await File.AppendAllTextAsync(rollout,
                Event("task_started", "stopped-turn") + Event("turn_aborted", "stopped-turn"));
            assert(reader.ReadDesktopSyncSnapshot(threadId).Status == "interrupted",
                "stopped Desktop turns remain distinct from failures");
            var missed = reader.ReadTerminalResultsAfter(threadId, "completed-turn");
            assert(missed.Select(t => t.TurnId).SequenceEqual(["running-turn", "failed-turn", "stopped-turn"]),
                "restart recovery returns every later completed/failed/stopped turn in chronological order");
            assert(reader.ReadTerminalResultsAfter(threadId, null).Single().TurnId == "stopped-turn",
                "first observation establishes the latest terminal baseline without replaying history");
            string cursorDirectory = Path.Combine(root.FullName, "cursors");
            var cursors = new FeishuReplyCursorStore(cursorDirectory);
            assert(cursors.Read("scope-a") is null, "a never-selected Task has no persisted cursor");
            cursors.Save("scope-a", "completed-turn");
            assert(new FeishuReplyCursorStore(cursorDirectory).Read("scope-a") == "completed-turn"
                && cursors.Read("scope-b") is null, "reply cursors survive restart without crossing identity scope");
            var subscriptions = new FeishuTaskSubscriptionStore(Path.Combine(root.FullName, "persistent-subscriptions"));
            subscriptions.Set("scope-a", threadId, new(threadId, root.FullName, "project-id"));
            assert(new FeishuTaskSubscriptionStore(Path.Combine(root.FullName, "persistent-subscriptions")).Read("scope-a").ContainsKey(threadId)
                && subscriptions.Read("scope-b").Count == 0, "Task subscriptions survive restart and remain identity scoped");
            subscriptions.Set("scope-a", threadId, null);
            assert(subscriptions.Read("scope-a").Count == 0, "unsubscribe durably removes the exact Task");

            var task = new CodexThreadSummary(
                threadId, "开始", "", root.FullName, "gpt", "notLoaded", [],
                ProjectId: "project-id", ProjectDisplayName: "Codex-Feishu-Bridge");
            using (JsonDocument card = JsonDocument.Parse(
                FeishuTaskManagementCardBuilder.BuildDesktopSyncConfirmation(
                    task,
                    task.ProjectDisplayName,
                    "completed",
                    "11111111111111111111111111111111",
                    "22222222222222222222222222222222",
                    "33333333333333333333333333333333")))
            {
                string body = card.RootElement.GetProperty("body").GetProperty("elements")[0]
                    .GetProperty("content").GetString() ?? "";
                assert(body.Contains("已有完成结果；接续后立即推送", StringComparison.Ordinal)
                    && body.Contains("完整历史仍保留在 Codex Desktop", StringComparison.Ordinal),
                    "the confirmation card truthfully distinguishes latest-result sync from full history");
            }

            CodexControlledRouteResult result = completed.TerminalResult(threadId)!;
            FeishuResultPart[] parts = FeishuDesktopSyncDelivery.TextParts(
                task.Title, result, index => "desktop-result-" + index);
            assert(parts.Length == 1
                && parts[0].Text.EndsWith("桌面已有的完整结果", StringComparison.Ordinal)
                && parts[0].Key == "desktop-result-0",
                "a completed handoff emits the exact latest assistant result with a stable key");
            FeishuResultPart[] emptyFailure = FeishuDesktopSyncDelivery.TextParts(
                task.Title,
                new(threadId, "failed", "failed", ""),
                index => "failure-" + index);
            assert(emptyFailure.Single().Text.Contains("运行失败", StringComparison.Ordinal),
                "a terminal Desktop failure always produces visible feedback");

            string storeDirectory = Path.Combine(root.FullName, "subscriptions");
            var subscription = new FeishuDesktopSyncSubscription(
                "generation", "owner", threadId, root.FullName, task.Title,
                task.ProjectDisplayName, task.ProjectId, "running-turn", "completed-turn",
                DateTimeOffset.UtcNow);
            var store = new FeishuDesktopSyncSubscriptionStore(storeDirectory);
            store.Save(subscription);
            byte[] persisted = File.ReadAllBytes(Path.Combine(storeDirectory, "subscriptions.bin"));
            assert(!Encoding.UTF8.GetString(persisted).Contains(threadId, StringComparison.Ordinal),
                "pending Desktop handoff state is encrypted at rest");
            var restarted = new FeishuDesktopSyncSubscriptionStore(storeDirectory);
            assert(restarted.Pending().Single() == subscription,
                "a running Desktop handoff survives Bridge restart with its exact turn checkpoint");
            FeishuDesktopSyncSubscription replacement = subscription with
            {
                ThreadId = "another-task",
                ExpectedTurnId = "another-turn",
                CreatedAt = subscription.CreatedAt.AddSeconds(1),
            };
            restarted.Save(replacement);
            assert(restarted.Pending().SequenceEqual([replacement]),
                "confirming another Desktop Task atomically replaces the old pending handoff");
            restarted.Remove(replacement);
            assert(restarted.Pending().Length == 0,
                "a persisted handoff is removed only after its terminal result is captured");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Event(string type, string turnId, string text = "") =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new { type, turn_id = turnId, last_agent_message = text },
        }) + "\n";
}
