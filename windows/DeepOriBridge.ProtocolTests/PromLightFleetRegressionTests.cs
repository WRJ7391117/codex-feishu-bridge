using DeepOriBridge.Services;
using DeepOriBridge.Models;

internal static class PromLightFleetRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        string directory = Directory.CreateTempSubdirectory("deepori-fleet-test-").FullName;
        try
        {
            var registry = new PromLightRegistry(Path.Combine(directory, "registry.bin"), b => b, b => b);
            var a = registry.Bind("alice", "gen", "device-a", "工作灯");
            var b = registry.Bind("alice", "gen", "device-b", "工作灯");
            var c = registry.Bind("bob", "gen", "device-c", "门口灯");
            assert(registry.Owned("alice", "gen").Length == 2 && registry.Owned("bob", "gen").Length == 1,
                "each authorized user can own multiple lamps without seeing another owner's lamps");
            assert(a.IsDefault && !b.IsDefault && c.IsDefault, "defaults are per owner");
            void Denied(Action action, string label) { try { action(); assert(false, label); } catch (InvalidOperationException) { assert(true, label); } }
            Denied(() => registry.Require("bob", "gen", a.Id), "another owner cannot address this lamp");
            Denied(() => registry.Require("alice", "new-gen", a.Id), "a replaced Bot cannot reuse old lamp capabilities");
            Denied(() => registry.Bind("bob", "gen", "DEVICE-A", "偷取"), "case aliases cannot claim an occupied endpoint");
            a = registry.Update("alice", "gen", a.Id, a.Revision, taskIds: ["task-one", "task-two"]);
            b = registry.Update("alice", "gen", b.Id, b.Revision, taskIds: ["task-one"]);
            var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            async Task BlockedWrite()
            {
                using var lease = await registry.AcquireWriteLeaseAsync(a.Id, deadline.Token);
                submitted.SetResult();
                await ack.Task.WaitAsync(deadline.Token);
            }
            Task write = BlockedWrite();
            await submitted.Task.WaitAsync(deadline.Token);
            var secondInstance = new PromLightRegistry(Path.Combine(directory, "registry.bin"), data => data, data => data);
            try
            {
                Denied(() => secondInstance.Update("alice", "gen", a.Id, a.Revision, unbind: true),
                    "an outstanding HID write prevents successful concurrent unbind even through another registry instance");
                Denied(() => registry.Update("alice", "gen", a.Id, a.Revision, taskIds: []),
                    "an outstanding HID write prevents concurrent task removal");
                b = registry.Update("alice", "gen", b.Id, b.Revision, name: "独立灯");
                assert(registry.Require("alice", "gen", a.Id).Revision == a.Revision && b.Name == "独立灯",
                    "busy lamp preserves its configuration without blocking another lamp");
            }
            finally { ack.TrySetResult(); await write; }
            a = registry.Update("alice", "gen", a.Id, a.Revision, name: "ACK 后可更新");
            assert(a.TaskIds.Length == 2 && b.TaskIds.Single() == "task-one", "task-to-lamp mapping is many to many");
            long oldRevision = a.Revision;
            a = registry.Update("alice", "gen", a.Id, a.Revision, name: "桌面灯");
            Denied(() => registry.Update("alice", "gen", a.Id, oldRevision, taskIds: []), "stale cards cannot erase newer bindings");
            b = registry.Update("alice", "gen", b.Id, b.Revision, makeDefault: true);
            a = registry.Require("alice", "gen", a.Id);
            assert(!a.IsDefault && b.IsDefault && registry.Require("bob", "gen", c.Id).IsDefault,
                "changing default neither affects another owner nor broadcasts task assignments");
            a = registry.Update("alice", "gen", a.Id, a.Revision, unbind: true);
            assert(a.PendingUnbind && a.TaskIds.Length == 0, "unbind first durably removes tasks but reserves the physical endpoint");
            Denied(() => registry.Bind("bob", "gen", "device-a", "再次占用"), "offline pending cleanup cannot be reassigned");
            assert(!registry.CompleteUnbind(a.Id, a.DeviceId, oldRevision, PromLightStatus.Idle, 65), "stale ACK cannot complete unbind");
            assert(!registry.CompleteUnbind(a.Id, "device-b", a.Revision, PromLightStatus.Idle, 65), "wrong physical endpoint ACK cannot complete unbind");
            assert(!registry.CompleteUnbind(a.Id, a.DeviceId, a.Revision, PromLightStatus.Running, 65), "non-idle ACK cannot release device");
            assert(!registry.CompleteUnbind(a.Id, a.DeviceId, a.Revision, PromLightStatus.Idle, 0), "zero byte write cannot release device");
            assert(registry.CompleteUnbind(a.Id, a.DeviceId, a.Revision, PromLightStatus.Idle, 65), "exact successful cleanup ACK releases endpoint");
            var reclaimed = registry.Bind("bob", "gen", "device-a", "新灯");
            assert(reclaimed.Id != a.Id && reclaimed.TaskIds.Length == 0, "rebind uses a new lamp identity without inheriting prior tasks");
            var restarted = new PromLightRegistry(Path.Combine(directory, "registry.bin"), b => b, b => b);
            assert(restarted.Require("alice", "gen", b.Id).TaskIds.Single() == "task-one", "per-lamp mapping survives restart");
            var origins = new PromLightTurnOrigins(Path.Combine(directory, "origins.bin"), b => b, b => b);
            origins.Record(new("gen", "bob", "task-one", "turn-one", DateTimeOffset.UtcNow));
            string? turnOwner = new PromLightTurnOrigins(Path.Combine(directory, "origins.bin"), b => b, b => b).Find("gen", "task-one", "turn-one");
            assert(turnOwner == "bob", "turn origin persists across Bridge restart");
            assert(PromLightOwnerStatusPolicy.Resolve(PromLightStatus.HumanGate, "bob", turnOwner, 2) == PromLightStatus.HumanGate
                && PromLightOwnerStatusPolicy.Resolve(PromLightStatus.HumanGate, "alice", turnOwner, 2) == PromLightStatus.Running,
                "Mac parity: only the owner required to act gets flashing HumanGate on a shared Task");
            assert(PromLightOwnerStatusPolicy.Resolve(PromLightStatus.HumanGate, "alice", null, 2) == PromLightStatus.Running,
                "unknown multi-owner origin is never guessed or broadcast as an owner-targeted gate");
            assert(PromLightOwnerStatusPolicy.Resolve(PromLightStatus.Running, "alice", "bob", 2) == PromLightStatus.Running,
                "normal shared-Task running status remains visible on both lamps");
            assert(origins.Find("other-gen", "task-one", "turn-one") is null && origins.Find("gen", "task-one", "turn-two") is null,
                "origin cannot leak across Bot generations or turns");
            Denied(() => origins.Record(new("gen", "alice", "task-one", "turn-one", DateTimeOffset.UtcNow)), "another user cannot overwrite recorded origin");
            var retryA = new PromLightRetryPolicy();
            var retryB = new PromLightRetryPolicy();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            assert(retryA.MayAttempt(PromLightStatus.Running, now, false), "first lamp write is immediately eligible");
            retryA.Failed(now, 1);
            assert(!retryA.MayAttempt(PromLightStatus.Running, now.AddSeconds(1), false)
                && retryB.MayAttempt(PromLightStatus.Idle, now, false), "offline A backs off while healthy B remains independent");
            for (int attempt = 0; attempt < 20; attempt++) retryA.Failed(now, 1);
            assert(retryA.NextAttempt == now.AddSeconds(60), "retry delay is bounded at 60 seconds per lamp");
            assert(retryA.MayAttempt(PromLightStatus.Error, now, false), "changed desired state replaces the stale offline write");
            retryA.Failed(now, 0.5); retryA.Succeeded();
            assert(retryA.MayAttempt(PromLightStatus.Error, now, false), "ACK resets retry state");
            var migrated = new PromLightRegistry(Path.Combine(directory, "migrated.bin"), b => b, b => b);
            foreach (object? invalid in new object?[] { null, "", "{broken", "{\"Version\":99,\"Name\":\"旧灯\",\"TaskIds\":[]}",
                "{\"Version\":1,\"Name\":\"旧灯\",\"TaskIds\":[\"../invalid\"]}" })
            {
                var legacy = PromLightLegacyConfiguration.Parse(invalid);
                assert(legacy.State == (invalid is null ? PromLightLegacyState.Missing : PromLightLegacyState.Invalid),
                    "legacy loader distinguishes missing from corrupt/unknown instead of returning empty success");
                Denied(() => legacy.Import(migrated, "alice", "gen", "pinned-legacy", _ => true),
                    "actual migration transaction rejects invalid legacy configuration");
                assert(migrated.All().Length == 0, "failed migration never creates a zero-subscription v2 lamp");
            }
            var configuration = PromLightLegacyConfiguration.Parse("{\"Version\":1,\"Name\":\"旧灯\",\"TaskIds\":[\"task-one\",\"task-two\"]}");
            Denied(() => configuration.Import(migrated, "alice", "gen", "pinned-legacy", task => task == "task-one"),
                "migration must authorize every legacy task before creating a lamp");
            var imported = configuration.Import(migrated, "alice", "gen", "pinned-legacy", _ => true);
            assert(new PromLightRegistry(Path.Combine(directory, "migrated.bin"), b => b, b => b).Require("alice", "gen", imported.Id).TaskIds.Length == 2,
                "legacy endpoint and all Task bindings migrate in one durable transaction");
            Denied(() => migrated.Bind("alice", "gen", "pinned-legacy", "duplicate", ["task-one"]), "migration cannot duplicate or overwrite existing device ownership");
            File.WriteAllText(Path.Combine(directory, "broken.bin"), "{\"Version\":99,\"Lamps\":[]}");
            try { new PromLightRegistry(Path.Combine(directory, "broken.bin"), b => b, b => b).All(); assert(false, "unknown registry version must fail closed"); }
            catch (InvalidDataException) { assert(true, "unknown registry version is preserved and fails closed"); }
        }
        finally { foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        foreach (var stage in Enum.GetValues<FeishuMenuConfigurationStage>())
        {
            assert(SetupCompletionGatePolicy.ObserveMenuReply(stage, true, true, true, true)
                == (stage == FeishuMenuConfigurationStage.PublishedPendingClientSync ? FeishuMenuConfigurationStage.VisibleVerified : stage),
                "only published menu plus successful authorized business reply can complete setup");
            assert(SetupCompletionGatePolicy.ObserveMenuReply(stage, false, true, true, true) == stage,
                "cross-generation menu reply cannot complete setup");
            assert(SetupCompletionGatePolicy.ObserveMenuReply(stage, true, false, true, true) == stage,
                "unowned menu reply cannot complete setup");
            assert(SetupCompletionGatePolicy.ObserveMenuReply(stage, true, true, false, true) == stage,
                "unknown menu event cannot complete setup");
            assert(SetupCompletionGatePolicy.ObserveMenuReply(stage, true, true, true, false) == stage,
                "failed business reply cannot complete setup");
        }
    }
}
