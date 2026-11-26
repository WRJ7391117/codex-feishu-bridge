using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record CodexDesktopProject(
    string Id,
    string AppServerId,
    string Name,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ThreadIds,
    bool LegacyAssignmentsPending = false);

// Desktop supplies UI IDs/names and the host mapping. Its explicit assignment
// takes precedence over projectless hints. Nonempty server projectId overrides this fallback;
// older, not-yet-migrated servers omit it even for real project members.
internal static class CodexDesktopProjectCatalog
{
    internal static IReadOnlyList<CodexDesktopProject> Read()
    {
        string codexDirectory = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string path = Path.Combine(codexDirectory, ".codex-global-state.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            return Parse(document.RootElement, "local:" + Path.GetFullPath(codexDirectory));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("暂时无法读取 Codex 项目列表，请刷新重试。");
        }
    }

    internal static IReadOnlyList<CodexDesktopProject> Parse(JsonElement state, string localHostKey)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("local-projects", out JsonElement projects)
            || projects.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("app-server-project-id-by-legacy-project-id-by-host", out JsonElement hosts)
            || hosts.ValueKind != JsonValueKind.Object
            || !hosts.TryGetProperty(localHostKey, out JsonElement mappings)
            || mappings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex 项目映射暂不可用，请在 Codex 中打开项目后刷新。");
        }
        var assignments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool legacyPending = state.TryGetProperty("app-server-projects-migration-by-host", out JsonElement migrationHosts)
            && migrationHosts.ValueKind == JsonValueKind.Object
            && migrationHosts.TryGetProperty(localHostKey, out JsonElement migration)
            && migration.TryGetProperty("threadAssignmentsMigrated", out JsonElement migrated)
            && migrated.ValueKind == JsonValueKind.False;
        if (state.TryGetProperty("thread-project-assignments", out JsonElement threads)
            && threads.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty thread in threads.EnumerateObject())
            {
                if (Text(thread.Value, "projectKind") != "local") continue;
                string projectId = Text(thread.Value, "projectId");
                if (!assignments.TryGetValue(projectId, out List<string>? ids))
                    assignments.Add(projectId, ids = []);
                ids.Add(thread.Name);
            }
        }
        // The Desktop's per-thread workspace state overrides its older global assignment.
        if (state.TryGetProperty("electron-persisted-atom-state", out JsonElement atoms)
            && atoms.ValueKind == JsonValueKind.Object)
        {
            const string prefix = "thread-workspace-state-v1:";
            foreach (JsonProperty atom in atoms.EnumerateObject().Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (atom.Value.ValueKind != JsonValueKind.Object) continue;
                if ((!atom.Value.TryGetProperty("projectMembership", out JsonElement membership) || membership.ValueKind == JsonValueKind.Null)
                    && !atom.Value.TryGetProperty("project", out membership)) continue;
                string threadId = atom.Name[prefix.Length..];
                foreach (List<string> ids in assignments.Values) ids.Remove(threadId);
                if (Text(membership, "projectKind") != "local") continue;
                string projectId = Text(membership, "projectId");
                if (!assignments.TryGetValue(projectId, out List<string>? target)) assignments.Add(projectId, target = []);
                target.Add(threadId);
            }
        }
        var result = new List<CodexDesktopProject>();
        foreach (JsonProperty entry in projects.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || Text(entry.Value, "id") != entry.Name
                || !mappings.TryGetProperty(entry.Name, out JsonElement mapping)
                || mapping.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(mapping.GetString())
                || !entry.Value.TryGetProperty("rootPaths", out JsonElement roots)
                || roots.ValueKind != JsonValueKind.Array) continue;
            string[] paths = roots.EnumerateArray()
                .Where(root => root.ValueKind == JsonValueKind.String)
                .Select(root => root.GetString()!)
                .Where(Path.IsPathFullyQualified)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string name = Text(entry.Value, "name");
            if (paths.Length == 0 || string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new CodexDesktopProject(entry.Name, mapping.GetString()!, name,
                paths, assignments.GetValueOrDefault(entry.Name) ?? [], legacyPending));
        }
        if (state.TryGetProperty("project-order", out JsonElement order) && order.ValueKind == JsonValueKind.Array)
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JsonElement id in order.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String) ranks.TryAdd(id.GetString()!, ranks.Count);
            return result.OrderBy(project => ranks.GetValueOrDefault(project.Id, int.MaxValue)).ToArray();
        }
        return result;
    }

    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement text)
        && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
}

internal static class CodexProjectMenuPolicy
{
    internal static CodexThreadSummary RequireSyncTask(CodexThreadSummary? current, string? taskId, string? projectId)
        => current is not null && current.ThreadId == taskId && !current.Archived && current.AuthorizedForRouting
            && !string.IsNullOrWhiteSpace(projectId) && current.EffectiveProjectId == projectId
            ? current : throw new InvalidOperationException("Task 或项目归属已变化、无法核实；未接续，请重新选择真实项目下的 Task。");
    internal static CodexDesktopProject SelectProject(IReadOnlyList<CodexDesktopProject> projects,
        string? requestedId, string? currentId)
    {
        if (!string.IsNullOrWhiteSpace(requestedId))
            return projects.SingleOrDefault(project => project.Id == requestedId)
                ?? throw new InvalidOperationException("项目已移除或授权已变化，请刷新项目列表。");
        return projects.FirstOrDefault(project => project.Id == currentId) ?? projects.FirstOrDefault()
            ?? throw new InvalidOperationException("当前授权范围内没有已保存的本机 Codex 项目。");
    }

    internal static string CreationDirectory(CodexDesktopProject project, string authorizedRoot)
        => project.Roots.FirstOrDefault(root => AuthorizedProjectScope.Contains(authorizedRoot, root))
            ?? throw new InvalidOperationException("项目中没有明确授权的工作目录；未创建 Task。");

    internal static string? MembershipNotice(IReadOnlyList<CodexThreadSummary> threads)
        => threads.Any(thread => string.IsNullOrWhiteSpace(thread.ProjectId))
            ? "部分 Task 按本机已保存的项目分配显示，可能尚未与当前 Codex 侧栏同步；请核对 Task 名称。目录未授权的 Task 不可操作。"
            : null;

    internal static CodexThreadSummary RequireActionableTask(IReadOnlyList<CodexThreadSummary> threads, string? id)
        => threads.SingleOrDefault(thread => thread.ThreadId == id && thread.AuthorizedForRouting && !thread.Archived)
            ?? throw new InvalidOperationException("Task 已移出项目、不可见或工作目录未授权；未执行操作。");
}
