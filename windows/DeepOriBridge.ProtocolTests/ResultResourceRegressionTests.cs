using System.Text;
using DeepOriBridge.Services;

internal static class ResultResourceRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("deepori-resource-result-tests-");
        try
        {
            string task = Directory.CreateDirectory(Path.Combine(root.FullName, "task")).FullName;
            string spool = Path.Combine(root.FullName, "spool");
            string source = Path.Combine(task, "中文结果.txt");
            string outside = Path.Combine(root.FullName, "outside.txt");
            await File.WriteAllTextAsync(source, "original snapshot");
            await File.WriteAllTextAsync(outside, "must not send");
            await File.WriteAllTextAsync(Path.Combine(task, "archive.zip"), "unsupported");
            var resources = new FeishuResultResources(spool);
            FeishuResultPart[] parts = await resources.CaptureAsync("turn", $"[结果](<{source}>)\n[重复](<{source}>)\n[越界](<{outside}>)\n[远程](https://example.com/a.png)\n[压缩](archive.zip)", task,
                i => "resource-" + i, CancellationToken.None);
            assert(parts.Length == 1 && parts[0].ResourceKind == "file" && parts[0].FileName == "中文结果.txt",
                "results accept authorized documents and deduplicate; reject outside/remote/unsupported links");
            assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(parts[0].ResourceFile!)).Contains("original snapshot"),
                "resource contents are encrypted at rest");
            await File.WriteAllTextAsync(source, "changed later");
            using (var input = new FeishuAttachmentSession())
            {
                string directory = Path.GetDirectoryName(input.NewDownloadPath())!;
                string local = await new FeishuResultResources(spool).MaterializeAsync(parts[0], directory, CancellationToken.None);
                assert(await File.ReadAllTextAsync(local) == "original snapshot", "restart/retry uses immutable original resource bytes");
            }
            var box = new FeishuTaskResultOutbox(Path.Combine(root.FullName, "outbox"));
            await box.SaveAsync("turn", new("generation", "owner", task, [new("result", "text"), ..parts]), CancellationToken.None);
            var sent = new List<string>();
            await box.SendPartAsync("turn", "text", _ => true, (_, p, _) => { sent.Add(p.Key); return Task.CompletedTask; }, CancellationToken.None);
            await new FeishuTaskResultOutbox(Path.Combine(root.FullName, "outbox")).FlushAsync(_ => true,
                (_, p, _) => { assert(p.ResourceFile == parts[0].ResourceFile, "resource snapshot reference survives outbox restart"); sent.Add(p.Key); return Task.CompletedTask; }, CancellationToken.None);
            assert(sent.SequenceEqual(["text", "resource-0"]), "text plus resource recovery preserves order without duplicate text");

            string cleanupDirectory;
            using (var input = new FeishuAttachmentSession())
            {
                string download = input.NewDownloadPath();
                cleanupDirectory = Path.GetDirectoryName(download)!;
                await File.WriteAllTextAsync(download, "temporary");
            }
            assert(!Directory.Exists(cleanupDirectory), "completed input session cleans only its owned directory");

            string retainedDirectory;
            using (var input = new FeishuAttachmentSession())
            {
                string download = input.NewDownloadPath();
                retainedDirectory = Path.GetDirectoryName(download)!;
                await File.WriteAllTextAsync(download, "may still be in use");
                input.MayBeInUse = true;
            }
            assert(Directory.Exists(retainedDirectory), "uncertain Desktop execution retains attachments rather than deleting in-use input");
            foreach (string file in Directory.GetFiles(retainedDirectory)) File.Delete(file);
            Directory.Delete(retainedDirectory);

            string rollout = Path.Combine(root.FullName, "rollout-test-task.jsonl");
            await File.WriteAllTextAsync(rollout,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn\"}}\n"
                + System.Text.Json.JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "image_generation_end", saved_path = Path.Combine(task, "result.png") } }) + "\n"
                + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn\"}}\n");
            var reader = new CodexRolloutTaskStateReader(root.FullName);
            assert(reader.ReadGeneratedImageLinks("test-task", "turn").Contains("result.png"), "generated image event without turn_id is scoped by exact start/completion boundaries");
            assert(reader.ReadGeneratedImageLinks("test-task", "other") == "", "generated images do not leak across turn IDs");
            string terminalDirectory;
            using (var input = new FeishuAttachmentSession("test-task", "fixture-queue"))
            {
                terminalDirectory = Path.GetDirectoryName(input.NewDownloadPath())!;
                input.RecordTurn("missing-turn");
                input.MayBeInUse = true;
                FeishuAttachmentSession.Sweep(reader, _ => true, [terminalDirectory]);
                assert(Directory.Exists(terminalDirectory), "another turn's result cannot clean an active attachment session");
                input.RecordTurn("turn");
            }
            FeishuAttachmentSession.Sweep(reader, _ => false, [terminalDirectory]);
            assert(!Directory.Exists(terminalDirectory), "restart cleanup follows the exact encrypted session-to-turn ownership");
            string unknownDirectory = Directory.CreateTempSubdirectory("deepori-feishu-input-").FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(unknownDirectory, "unowned.txt"), "must survive");
                FeishuAttachmentSession.Sweep(reader, _ => false, [unknownDirectory]);
                assert(File.Exists(Path.Combine(unknownDirectory, "unowned.txt")), "unmarked legacy files are never swept by guessed ownership");
            }
            finally { File.Delete(Path.Combine(unknownDirectory, "unowned.txt")); Directory.Delete(unknownDirectory); }
        }
        finally { root.Delete(recursive: true); }
    }
}
