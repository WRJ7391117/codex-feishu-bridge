using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

internal static class ProjectListRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        string root = Path.Combine(Path.GetTempPath(), "deepori-project-fixture-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "deepori-outside-" + Guid.NewGuid().ToString("N"));
        const string firstId = "9b42b1f3-d062-48d6-af33-04177cb14f90";
        const string secondId = "72bd5bca-2985-45ac-b8ae-9477a388d5cc";
        const string staleId = "01a06ecf-f884-79e1-84a4-f31e5a1cbdd8";
        var first = new CodexDesktopProject(firstId, "server-first", "Codex-Feishu-Bridge", [root], ["legacy-member", "saved-archived"]);
        var second = new CodexDesktopProject(secondId, "server-second", "DeepOri Bridge", [root, Path.GetPathRoot(root)!], []);
        CodexDesktopProject[] projects = [first, second];
        JsonElement cached = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["local-projects"] = new Dictionary<string, object>
            {
                [secondId] = new { id = secondId, name = second.Name, rootPaths = second.Roots },
                [firstId] = new { id = firstId, name = first.Name, rootPaths = new[] { root } },
            },
            ["app-server-project-id-by-legacy-project-id-by-host"] = new Dictionary<string, object>
                { ["local:test"] = new Dictionary<string, string> { [firstId] = first.AppServerId, [secondId] = second.AppServerId } },
            ["project-order"] = new[] { firstId, "deleted-project", firstId, secondId },
            ["projectless-thread-ids"] = new[] { staleId },
            ["thread-project-assignments"] = new Dictionary<string, object>
            {
                [staleId] = new { projectKind = "local", projectId = firstId },
                ["legacy-member"] = new { projectKind = "local", projectId = firstId },
            },
        });
        IReadOnlyList<CodexDesktopProject> catalog = CodexDesktopProjectCatalog.Parse(cached, "local:test");
        var migrationFixture = System.Text.Json.Nodes.JsonNode.Parse(cached.GetRawText())!;
        migrationFixture["app-server-projects-migration-by-host"] = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"local:test\":{\"threadAssignmentsMigrated\":false}}");
        migrationFixture["electron-persisted-atom-state"] = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"thread-workspace-state-v1:legacy-member\":{\"projectMembership\":null,\"project\":{\"projectKind\":\"local\",\"projectId\":\"" + secondId + "\"}}}");
        var migratedCatalog = CodexDesktopProjectCatalog.Parse(JsonSerializer.SerializeToElement(migrationFixture), "local:test");
        assert(migratedCatalog.All(p => p.LegacyAssignmentsPending)
            && !migratedCatalog[0].ThreadIds.Contains("legacy-member") && migratedCatalog[1].ThreadIds.Contains("legacy-member"),
            "explicit pending migration enables legacy membership and per-thread workspace overrides take precedence");
        assert(catalog.Select(project => project.Id).SequenceEqual([firstId, secondId]),
            "saved project-order overrides dictionary order, ignores missing IDs and deduplicates IDs");
        assert(catalog.Single(project => project.Id == firstId).ThreadIds.SequenceEqual([staleId, "legacy-member"]),
            "verified Desktop semantics preserve explicit assignment over projectless hints");
        assert(CodexProjectMenuPolicy.SelectProject(projects, secondId, firstId) == second,
            "explicit real project ID wins even when two saved projects share their primary directory");
        assert(CodexProjectMenuPolicy.SelectProject(projects, null, secondId) == second,
            "all menus default to the explicitly selected project without changing the current Task");
        assert(CodexProjectMenuPolicy.SelectProject(projects, null, "removed") == first,
            "a removed current project only changes the default menu filter");
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            assert(rejected, description);
        }
        Reject(() => CodexProjectMenuPolicy.SelectProject(projects, "removed", firstId), "stale explicit project selection fails closed");
        Reject(() => CodexProjectMenuPolicy.SelectProject([], null, null), "empty project catalog reports no available project");
        assert(CodexProjectMenuPolicy.CreationDirectory(second, root) == root,
            "new Task uses an authorized root, never the project's extra drive root");
        Reject(() => CodexProjectMenuPolicy.CreationDirectory(second with { Roots = [outside] }, root),
            "new Task cannot use an unauthorized project root");
        JsonElement creation = JsonSerializer.SerializeToElement(CodexAppServerClient.CreateNamedThreadParameters(root, second.AppServerId));
        assert(creation.GetProperty("projectId").GetString() == second.AppServerId
            && creation.GetProperty("cwd").GetString() == root
            && creation.GetProperty("sandbox").GetString() == "workspace-write"
            && !creation.TryGetProperty("runtimeWorkspaceRoots", out _),
            "new Task sends the chosen server project ID and only the authorized cwd without expanding roots");

        object Thread(string id, string? project, string? title = null, string? cwd = null, bool archived = false) => new
        {
            id, projectId = project, name = title ?? "Task " + id, preview = "preview must not replace title",
            cwd = cwd ?? root, threadSource = "agent_created_thread", status = new { type = "idle" }, archived,
        };
        var reads = new List<string>();
        var pages = new List<string>();
        Task<JsonElement> Request(string method, object parameters, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            JsonElement args = JsonSerializer.SerializeToElement(parameters);
            if (method == "thread/list")
            {
                if (!args.TryGetProperty("projectId", out _))
                {
                    assert(!args.GetProperty("archived").GetBoolean() && args.GetProperty("useStateDbOnly").GetBoolean(),
                        "saved assignments are intersected with the paginated global nonarchived list");
                    pages.Add("global");
                    if (args.GetProperty("cursor").ValueKind == JsonValueKind.Null)
                        return Task.FromResult(JsonSerializer.SerializeToElement(new
                        {
                            data = new[] { Thread("unassigned-not-a-member", null) }, nextCursor = "global-next",
                        }));
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        data = new[] { Thread("legacy-member", null), Thread(staleId, null, "开始"), Thread("unassigned-not-a-member", null) },
                        nextCursor = (string?)null,
                    }));
                }
                string project = args.GetProperty("projectId").GetString()!;
                assert(project == first.AppServerId || project == second.AppServerId, "list queries carry exact server project identities");
                assert(args.GetProperty("useStateDbOnly").GetBoolean() && !args.GetProperty("archived").GetBoolean()
                    && !args.TryGetProperty("cwd", out _), "current state DB membership is queried independently of Task cwd");
                string? cursor = args.GetProperty("cursor").GetString();
                pages.Add(project + ":" + cursor);
                if (project == second.AppServerId)
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { data = new[] { Thread("outside", project, "实际标题 中文🙂", outside) }, nextCursor = (string?)null }));
                if (cursor is null)
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        data = Enumerable.Range(0, 55).Select(i => Thread("member-" + i, project,
                            i == 0 ? "Windows Bridge 默认 Task 也是真实标题" : null)).ToArray(), nextCursor = "page-2",
                    }));
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        Thread("member-0", project), Thread("new-member", project), Thread(staleId, null, "开始"),
                        Thread("wrong-project", second.AppServerId), Thread("moved-during-read", project),
                        Thread("sparse-member", project), Thread("deleted", project), Thread("now-archived", project),
                    }, nextCursor = (string?)null,
                }));
            }
            assert(method == "thread/read" && !args.GetProperty("includeTurns").GetBoolean(),
                "membership hydration is metadata-only and never resumes or executes a Task");
            string id = args.GetProperty("threadId").GetString()!;
            reads.Add(id);
            if (id == "deleted") throw new CodexAppServerRequestException(-32600, "thread not loaded: deleted");
            string? membership = id switch
            {
                "outside" or "moved-during-read" => second.AppServerId,
                "sparse-member" or "legacy-member" or staleId => null,
                _ => first.AppServerId,
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                thread = Thread(id, membership, id switch
                {
                    "outside" => "实际标题 中文🙂",
                    "member-0" => "Windows Bridge 默认 Task 也是真实标题",
                    _ => null,
                }, id == "outside" ? outside : root, id == "now-archived"),
            }));
        }
        IReadOnlyList<CodexThreadSummary> firstThreads = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(first, root, Request, CancellationToken.None);
        IReadOnlyList<CodexThreadSummary> secondThreads = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(second, root, Request, CancellationToken.None);
        assert(firstThreads.Count == 56 && pages.Count == 3, "only authoritative project pagination supplies membership, without a legacy global-list fallback");
        assert(!reads.Contains("saved-archived") && !reads.Contains("unassigned-not-a-member"),
            "readable old rollouts and same-cwd unassigned Tasks do not establish current explicit membership");
        assert(!firstThreads.Any(t => t.ThreadId == "legacy-member" || t.ThreadId == "sparse-member"),
            "null hydrated project IDs cannot be made actionable by cached assignments or a stale list entry");
        assert(firstThreads.Any(t => t.ThreadId == "new-member") && !first.ThreadIds.Contains("new-member"),
            "current service membership includes new assignments missing from the legacy Desktop cache");
        assert(!firstThreads.Any(t => t.ThreadId == staleId) && !reads.Contains(staleId) && !reads.Contains("wrong-project"),
            "stale cached 开始 assignment and another project's Tasks never become members");
        assert(!firstThreads.Any(t => t.ThreadId is "moved-during-read" or "deleted" or "now-archived"),
            "explicitly moved, unavailable and archived Tasks cannot survive hydration");
        assert(firstThreads[0].Title == "Windows Bridge 默认 Task 也是真实标题",
            "actual names are preserved without title-based placeholder exclusions");
        assert(firstThreads.All(t => t.AuthorizedForRouting && t.ProjectDisplayName == first.Name)
            && secondThreads.Single().ProjectDisplayName == second.Name && !secondThreads.Single().AuthorizedForRouting,
            "project membership and display name stay separate from unchanged routing authorization");
        assert(CodexProjectMenuPolicy.MembershipNotice(firstThreads) is null,
            "confirmed project list contains no legacy-only membership");
        string? notice = CodexProjectMenuPolicy.MembershipNotice([new("unconfirmed", "old", "", root, "", "idle", [])]);
        assert(CodexProjectMenuPolicy.MembershipNotice(secondThreads) is null,
            "nonempty authoritative server membership does not need a saved-state warning");
        var savedConflict = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(
            first with { ThreadIds = [staleId] }, root, Request, CancellationToken.None);
        assert(!savedConflict.Any(thread => thread.ThreadId == staleId),
            "the saved 开始 assignment can no longer inject a projectless Task into the project selector");
        var supplement = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(first, root, Request, CancellationToken.None,
            _ => new HashSet<string>(["empty-named", "new-member", "now-archived", "moved-during-read", staleId]));
        assert(supplement.Any(t => t.ThreadId == "empty-named") && supplement.Count == 2,
            "current nonarchived SQLite membership restores empty named Tasks omitted by thread/list, with read-time membership revalidation");
        int snapshots = 0;
        var moved = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(first, root, Request, CancellationToken.None,
            _ => ++snapshots == 1 ? new HashSet<string>(["empty-named"]) : new HashSet<string>());
        assert(moved.Count == 0, "a Task removed or archived during hydration cannot survive final DB revalidation");
        var pendingMigration = first with { LegacyAssignmentsPending = true };
        var legacy = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(pendingMigration, root, Request, CancellationToken.None,
            id => id == "" ? new HashSet<string>(["legacy-member", "unassigned-not-a-member"])
                : new HashSet<string>(["new-member"]), () => pendingMigration);
        assert(legacy.Count == 2 && legacy.Single(t => t.ThreadId == "legacy-member").EffectiveProjectId == first.AppServerId
            && legacy.Single(t => t.ThreadId == "legacy-member").ProjectId == "",
            "verified pending migration restores live saved members while preserving raw null server membership");
        assert(!legacy.Any(t => t.ThreadId is "saved-archived" or "unassigned-not-a-member"),
            "legacy fallback cannot revive archived records or infer membership from cwd");
        var removedLegacy = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(pendingMigration, root, Request, CancellationToken.None,
            id => id == "" ? new HashSet<string>(["legacy-member"]) : new HashSet<string>(),
            () => pendingMigration with { ThreadIds = [] });
        assert(removedLegacy.Count == 0, "live assignment removal during hydration rejects legacy membership");
        var migratedLegacy = await CodexAppServerClient.ReadDesktopProjectThreadsAsync(pendingMigration, root, Request, CancellationToken.None,
            id => id == "" ? new HashSet<string>(["legacy-member"]) : new HashSet<string>(),
            () => pendingMigration with { LegacyAssignmentsPending = false });
        assert(migratedLegacy.Count == 0, "completed migration disables legacy membership even during an in-flight list");
        assert(CodexProjectMenuPolicy.RequireSyncTask(legacy.Last(), legacy.Last().ThreadId, first.AppServerId) == legacy.Last(),
            "sync accepts freshly verified legacy projection without pretending server membership is nonnull");
        CodexThreadSummary verified = firstThreads.First(t => t.ThreadId == "new-member");
        assert(CodexProjectMenuPolicy.RequireSyncTask(verified, verified.ThreadId, verified.ProjectId) == verified,
            "desktop sync accepts only the exact verified Task and project pair");
        Reject(() => CodexProjectMenuPolicy.RequireSyncTask(verified, staleId, verified.ProjectId), "an old Task confirmation cannot silently retarget another Task");
        Reject(() => CodexProjectMenuPolicy.RequireSyncTask(verified, verified.ThreadId, second.AppServerId), "a project move invalidates the old sync capability");
        Reject(() => CodexProjectMenuPolicy.RequireSyncTask(verified with { ProjectId = "" }, verified.ThreadId, ""), "legacy null-project confirmations fail closed");
        Reject(() => CodexProjectMenuPolicy.RequireSyncTask(verified with { Archived = true }, verified.ThreadId, verified.ProjectId), "archived Tasks cannot be confirmed");
        Reject(() => CodexProjectMenuPolicy.RequireSyncTask(verified with { AuthorizedForRouting = false }, verified.ThreadId, verified.ProjectId), "sync cannot broaden working-directory authorization");
        Reject(() => CodexProjectMenuPolicy.RequireActionableTask(secondThreads, "outside"),
            "visible out-of-scope Task cannot be selected, subscribed or associated");
        Reject(() => CodexProjectMenuPolicy.RequireActionableTask(firstThreads, "outside"), "cross-project Task token is rejected");
        assert(CodexProjectMenuPolicy.RequireActionableTask(firstThreads, "new-member").ThreadId == "new-member", "explicit authorized member is actionable");

        foreach (string scenario in new[] { "repeated-cursor", "malformed", "permission-error" })
        {
            bool rejected = false;
            try
            {
                await CodexAppServerClient.ReadDesktopProjectThreadsAsync(first, root, (method, args, token) =>
                {
                    if (scenario == "permission-error") throw new CodexAppServerRequestException(-32000, "denied");
                    return Task.FromResult(scenario == "malformed" ? JsonSerializer.SerializeToElement(new { data = 42 })
                        : JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = "same" }));
                }, CancellationToken.None);
            }
            catch (InvalidOperationException) { rejected = true; }
            assert(rejected, "incomplete or failed membership reads never fall back to stale cache: " + scenario);
        }

        FeishuProjectCardOption[] choices = [new(first.Name, "project-first", false), new(second.Name, "project-second", true)];
        var selectable = new CodexThreadSummary("safe", "原始 Task 中文🙂", "", root, "", "idle", [], ProjectDisplayName: second.Name);
        CodexThreadSummary blocked = secondThreads.Single();
        string[] cards =
        [
            FeishuTaskManagementCardBuilder.BuildSubscriptions(second.Name, "unused-project-token",
                [new(selectable.Title, second.Name, "safe", true), new(blocked.Title, second.Name, "BLOCKED", false, false, false, outside)],
                [], "add", "remove", "refresh", projects: choices, membershipNotice: notice),
            FeishuPromLightCardBuilder.BuildTaskManager("提示灯", second.Name, "unused-project-token",
                [new(selectable.Title, second.Name, "safe", true, false), new(blocked.Title, second.Name, "BLOCKED", false, false, false, outside)],
                [], "add", "remove", "back", "legend", projects: choices, membershipNotice: notice),
            FeishuTaskSelectorCardBuilder.Build(choices,
                [new(selectable.Title, second.Name, "safe", true), new(blocked.Title, second.Name, "BLOCKED", false, false, outside)], "refresh", false, membershipNotice: notice),
            FeishuTaskManagementCardBuilder.BuildNewTask(second.Name, "unused-project-token", "begin", "cancel", projects: choices, membershipNotice: notice),
        ];
        foreach (string card in cards)
        {
            using JsonDocument document = JsonDocument.Parse(card);
            JsonElement[] elements = document.RootElement.GetProperty("body").GetProperty("elements").EnumerateArray().ToArray();
            JsonElement projectSelector = elements.First(e => e.TryGetProperty("name", out JsonElement n) && n.GetString()!.Contains("project_selector"));
            assert(projectSelector.GetProperty("options").GetArrayLength() == 2
                && projectSelector.GetProperty("initial_option").GetString() == "project-second",
                "subscriptions, PromLight, selector and new Task show both real projects with a valid selected option");
            assert(projectSelector.GetProperty("options")[0].GetProperty("text").GetProperty("content").GetString() == first.Name
                && projectSelector.GetProperty("options")[1].GetProperty("text").GetProperty("content").GetString() == second.Name,
                "project names remain distinct instead of collapsing to a directory label");
            foreach (JsonElement selector in elements.Where(e => e.TryGetProperty("tag", out JsonElement tag) && tag.GetString() == "select_static"))
                assert(selector.GetProperty("options").EnumerateArray().All(o => o.GetProperty("value").GetString() != "BLOCKED"),
                    "out-of-scope Tasks have no selectable capability in any menu");
            if (elements.Any(e => e.TryGetProperty("name", out JsonElement n) && n.GetString()!.Contains("task_selector")))
            {
                assert(elements.Any(e => e.TryGetProperty("content", out JsonElement c) && c.GetString()!.Contains("目录未授权")),
                    "visible but unauthorized membership is explained instead of broadening scope");
                assert(elements.Any(e => e.TryGetProperty("content", out JsonElement c) && c.GetString()!.Contains("本机已保存")),
                    "all Task menus render the saved-membership caveat");
            }
        }
        string empty = FeishuTaskManagementCardBuilder.BuildSubscriptions(first.Name, "p", [], [], "a", "r", "f", projects: choices);
        using JsonDocument emptyCard = JsonDocument.Parse(empty);
        assert(!emptyCard.RootElement.GetProperty("body").GetProperty("elements").EnumerateArray()
            .Any(e => e.TryGetProperty("name", out JsonElement n) && n.GetString() == "subscription_task_selector"),
            "empty project has no fabricated Task option");
        using JsonDocument currentCard = JsonDocument.Parse(FeishuCurrentTaskCardBuilder.Build(
            selectable.Title, second.Name, "空闲", 0, 0, DateTimeOffset.UtcNow, "refresh", "switch", membershipNotice: notice));
        assert(currentCard.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()!.Contains("本机已保存"),
            "current Task card also exposes fallback membership uncertainty");
    }
}
