using System.Collections.Concurrent;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

public sealed partial class FeishuEventRuntime
{
    private const string FleetPrefix = "deepori.card.promlight.fleet.";
    private readonly ConcurrentDictionary<string, MenuCardAction> _pendingLampNames = new();

    private string LampToken(string owner, string operation, PromLightLamp lamp, string? task = null,
        string? project = null, string? root = null) =>
        CreateMenuCardCapability(owner, FleetPrefix + operation, task, project, root, lamp);

    private object LampButton(string owner, string operation, string label, PromLightLamp lamp, bool confirm = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["tag"] = "button", ["text"] = new { tag = "plain_text", content = label },
            ["type"] = "default", ["name"] = FleetPrefix + operation,
            ["value"] = new { deepori_token = LampToken(owner, operation, lamp) },
        };
        if (confirm) result["confirm"] = new { title = new { tag = "plain_text", content = "确认解绑这盏灯？" },
            text = new { tag = "plain_text", content = "清除 Task 关联后等待清灯写入确认；离线时保留归属。不会删除蓝牙配对。" } };
        return result;
    }

    private async Task<string> BuildFleetCardAsync(string owner, IPromLightFleet fleet, CancellationToken token, int page = 0)
    {
        var elements = new List<object>();
        var lamps = fleet.Owned(owner);
        page = Math.Clamp(page, 0, Math.Max(0, (lamps.Length - 1) / 12));
        foreach (PromLightLamp lamp in lamps.Skip(page * 12).Take(12))
        {
            if (lamp.PendingUnbind)
            {
                elements.Add(new { tag = "markdown", content = JsonSafeLampName(lamp.Name) + " · 等待清灯确认后解绑；尚未释放给其他使用者。" });
                continue;
            }
            IPromLightService service = fleet.ForLamp(owner, lamp.Id);
            string status;
            try
            {
                token.ThrowIfCancellationRequested();
                var activity = service.Activity;
                status = activity.SubmittedAt > DateTimeOffset.UtcNow.AddSeconds(-30)
                    ? "最近写入已确认 · " + FeishuPromLightCardBuilder.StatusLabel(activity.Status)
                    : "离线或尚未确认写入";
            }
            catch (Exception) { status = "暂不可用；其他灯不受影响"; }
            elements.Add(new { tag = "markdown", content = "**" + JsonSafeLampName(lamp.Name) + "**"
                + (lamp.IsDefault ? " · 默认灯" : "") + "\n" + status + $"\n关联 {lamp.TaskIds.Length} 个 Task；写入确认不等于灯效回读。" });
            elements.Add(LampButton(owner, "tasks", "关联 Task · " + lamp.Name, lamp));
            elements.Add(LampButton(owner, "settings", "设备设置 · " + lamp.Name, lamp));
        }
        if (lamps.Length == 0) elements.Add(new { tag = "markdown", content = "你尚未绑定提示灯。请在 Windows App 中选择具体设备和使用者；不会自动占用其他人的灯。" });
        if (page > 0) elements.Add(CreateCardButton(owner, FleetPrefix + "page", "上一页", "default", taskId: (page - 1).ToString()));
        if ((page + 1) * 12 < lamps.Length) elements.Add(CreateCardButton(owner, FleetPrefix + "page", "下一页", "default", taskId: (page + 1).ToString()));
        elements.Add(CreateCardButton(owner, "deepori.card.promlight.refresh", "刷新我的提示灯", "default"));
        elements.Add(CreateCardButton(owner, "deepori.card.promlight.legend", "灯光说明", "default"));
        await Task.CompletedTask;
        return FeishuMacParityCardBuilder.BuildNotice("我的提示灯", $"{lamps.Length} 盏 · 第 {page + 1} 页 · 使用者独立管理",
            "每盏灯独立关联 Task。默认灯只用于选择；离线时不会切换到另一盏灯。", elements);
    }

    private async Task HandleFleetCardAsync(string eventId, string owner, MenuCardAction action,
        IPromLightFleet fleet, CancellationToken token)
    {
        if (action.Action == FleetPrefix + "page")
        {
            if (!int.TryParse(action.TaskId, out int page)) throw new InvalidOperationException("页码无效。");
            await SendP2PCardAsync(owner, await BuildFleetCardAsync(owner, fleet, token, page), BuildIdempotencyKey(eventId, "fleet-page"), token);
            return;
        }
        if (action.Action is "deepori.card.promlight.refresh" or "deepori.card.promlight.back")
        {
            await ShowMenuCardAsync(eventId, owner, "promlight", token); return;
        }
        if (action.Action == "deepori.card.promlight.legend")
        {
            await ShowMenuCardAsync(eventId, owner, "promlight_legend", token); return;
        }
        if (!action.Action.StartsWith(FleetPrefix, StringComparison.Ordinal) || action.LampId is null || action.LampRevision is null)
            throw new InvalidOperationException("旧单灯卡片已失效，请重新打开我的提示灯。");
        PromLightLamp lamp = fleet.Require(owner, action.LampId, action.LampRevision);
        string operation = action.Action[FleetPrefix.Length..];
        if (operation is "tasks" or "project" or "select" or "add" or "remove")
        {
            if (action.AuthorizedProjectRoot is not null) RequireMenuProjectAuthorization(owner, action.AuthorizedProjectRoot);
            if (operation is "add" or "remove")
            {
                ProjectMenuSnapshot menu = await GetProjectMenuAsync(owner, action.ProjectFilter, token);
                var task = CodexProjectMenuPolicy.RequireActionableTask(menu.Threads, action.TaskId ?? "");
                RequireMenuProjectAuthorization(owner, menu.AuthorizedRoot);
                fleet.ForLamp(owner, lamp.Id, lamp.Revision).SetTaskAssociation(task.ThreadId, operation == "add");
                lamp = fleet.Require(owner, lamp.Id);
                await fleet.ForLamp(owner, lamp.Id).RefreshAsync(token);
            }
            string card = await BuildFleetTaskCardAsync(owner, fleet, lamp, action.ProjectFilter, action.TaskId, token);
            await SendP2PCardAsync(owner, card, BuildIdempotencyKey(eventId, "fleet-tasks"), token);
            return;
        }
        if (operation == "default") fleet.MakeDefault(owner, lamp.Id, lamp.Revision);
        else if (operation == "unbind") await fleet.UnbindAsync(owner, lamp.Id, lamp.Revision, token);
        else if (operation == "rename")
        {
            foreach (var item in _pendingLampNames.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow)) _pendingLampNames.TryRemove(item.Key, out _);
            _pendingLampNames[owner] = action;
            await SendP2PMessageAsync(owner, "请在 5 分钟内发送“灯名：新名称”（最多 40 字）。只有这个前缀会用于改名，其他消息仍发给 Task。",
                BuildIdempotencyKey(eventId, "fleet-rename"), token);
            return;
        }
        else if (operation == "settings")
        {
            string card = FeishuMacParityCardBuilder.BuildNotice("提示灯设置", lamp.Name,
                "改名只影响显示；默认灯按使用者分别保存。解绑只影响这盏灯。",
                [LampButton(owner, "rename", "修改名称", lamp), LampButton(owner, "default", "设为我的默认灯", lamp),
                    LampButton(owner, "unbind", "解绑此灯", lamp, confirm: true),
                    CreateCardButton(owner, "deepori.card.promlight.back", "返回我的提示灯", "default")]);
            await SendP2PCardAsync(owner, card, BuildIdempotencyKey(eventId, "fleet-settings"), token);
            return;
        }
        else throw new InvalidOperationException("未识别的逐灯操作。");
        await ShowMenuCardAsync(eventId, owner, "promlight", token);
    }

    private async Task<string> BuildFleetTaskCardAsync(string owner, IPromLightFleet fleet, PromLightLamp lamp,
        string? projectFilter, string? selectedTask, CancellationToken token)
    {
        ProjectMenuSnapshot menu = await GetProjectMenuAsync(owner, projectFilter, token);
        lamp = fleet.Require(owner, lamp.Id, lamp.Revision);
        CodexThreadSummary? selected = menu.Threads.FirstOrDefault(t => t.AuthorizedForRouting && t.ThreadId == selectedTask)
            ?? menu.Threads.FirstOrDefault(t => t.AuthorizedForRouting);
        var options = menu.Threads.Select(t => new FeishuPromLightTaskOption(t.Title, menu.Project.Name,
            t.AuthorizedForRouting ? LampToken(owner, "select", lamp, t.ThreadId, menu.Project.Id, menu.AuthorizedRoot) : "",
            t.ThreadId == selected?.ThreadId, lamp.TaskIds.Contains(t.ThreadId), t.AuthorizedForRouting, t.WorkingDirectory)).ToArray();
        var projects = menu.Projects.Select(p => new FeishuProjectCardOption(p.Name,
            LampToken(owner, "project", lamp, null, p.Id, menu.AuthorizedRoot), p.Id == menu.Project.Id)).ToArray();
        // Builder action names are presentation only; callbacks must match the stored capability.
        string card = FeishuPromLightCardBuilder.BuildTaskManager(lamp.Name, menu.Project.Name,
            LampToken(owner, "project", lamp, null, menu.Project.Id, menu.AuthorizedRoot), options,
            AssociatedTaskLabels(lamp.Binding, menu.Threads),
            LampToken(owner, "add", lamp, selected?.ThreadId, menu.Project.Id, menu.AuthorizedRoot),
            LampToken(owner, "remove", lamp, selected?.ThreadId, menu.Project.Id, menu.AuthorizedRoot),
            CreateMenuCardCapability(owner, "deepori.card.promlight.back", null, null),
            CreateMenuCardCapability(owner, "deepori.card.promlight.legend", null, null), projects: projects,
            membershipNotice: CodexProjectMenuPolicy.MembershipNotice(menu.Threads));
        return card.Replace("deepori.card.promlight.tasks.add", FleetPrefix + "add", StringComparison.Ordinal)
            .Replace("deepori.card.promlight.tasks.remove", FleetPrefix + "remove", StringComparison.Ordinal);
    }

    private async Task CompleteLampRenameAsync(string messageId, string owner, string name, CancellationToken token)
    {
        if (!_pendingLampNames.TryRemove(owner, out var action)) return;
        string text;
        try
        {
            if (_promLight is not IPromLightFleet fleet || action.ExpiresAt <= DateTimeOffset.UtcNow
                || action.Generation != _sidecarAttestation?.IdentityGeneration || action.LampId is null || action.LampRevision is null)
                throw new InvalidOperationException("改名操作已过期；请重新打开该灯的设置。");
            fleet.Rename(owner, action.LampId, action.LampRevision.Value, name);
            text = "提示灯名称已更新。";
        }
        catch (Exception exception) { text = exception is InvalidOperationException ? exception.Message : "改名失败；未改变其他灯。"; }
        try { await SendP2PMessageAsync(owner, text, BuildIdempotencyKey(messageId, "fleet-renamed"), token); }
        catch (Exception) { /* Delivery failure must not replay a configuration mutation. */ }
    }

    private static string JsonSafeLampName(string name)
    {
        foreach (char c in "\\`*_[]<>#") name = name.Replace(c.ToString(), "\\" + c);
        return name;
    }
}
