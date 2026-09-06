using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record FeishuManagedTaskOption(
    string Title,
    string Project,
    string CapabilityToken,
    bool IsSelected,
    bool IsEnabled = false,
    bool IsSelectable = true,
    string WorkingDirectory = "");

internal sealed record FeishuModelOption(
    string Label,
    string CapabilityToken,
    bool IsSelected);

internal static class FeishuTaskManagementCardBuilder
{
    internal static string BuildNewTask(
        string project,
        string projectToken,
        string beginToken,
        string cancelToken,
        bool waitingForTitle = false,
        bool canceled = false,
        IReadOnlyList<FeishuProjectCardOption>? projects = null,
        string? membershipNotice = null)
    {
        var elements = new List<object>
        {
            Markdown(canceled
                ? "已取消新建流程，不会再等待 Task 标题。已经创建的 Task 不受影响。"
                : waitingForTitle
                    ? $"已选择项目：**{Escape(project)}**\n\n请直接发送 Task 标题（1～120 个字符）。这条消息只用于创建 Task。"
                    : "**选择新 Task 所属项目**\n确认项目后，Bot 会等待你发送 Task 标题。"),
        };
        if (!canceled && !string.IsNullOrWhiteSpace(membershipNotice)) elements.Add(Markdown(Escape(membershipNotice)));
        if (!canceled && !waitingForTitle)
        {
            elements.Add(Selector("new_task_project_selector", "选择项目",
                (projects ?? [new(project, projectToken, true)]).Select(item => Option(item.Name, item.CapabilityToken)).ToArray(),
                projects?.SingleOrDefault(item => item.IsSelected)?.CapabilityToken ?? projectToken));
            elements.Add(Button("在此项目新建", "deepori.card.new.begin", beginToken, "primary_filled"));
            elements.Add(Button("取消新建", "deepori.card.new.cancel", cancelToken));
        }
        else if (waitingForTitle)
        {
            elements.Add(Button("取消新建", "deepori.card.new.cancel", cancelToken));
        }
        else
        {
            elements.Add(Button("返回 Task 选择", "deepori.card.selector.open", cancelToken));
        }
        return Card("新建 Codex Task", canceled ? "已取消" : $"当前项目：{Safe(project, 80)}", canceled ? "grey" : "blue", elements);
    }

    internal static string BuildSubscriptions(
        string project,
        string projectToken,
        IReadOnlyList<FeishuManagedTaskOption> tasks,
        IReadOnlyList<string> subscribedLabels,
        string addToken,
        string removeToken,
        string refreshToken,
        string? change = null,
        IReadOnlyList<FeishuProjectCardOption>? projects = null,
        string? membershipNotice = null)
    {
        FeishuManagedTaskOption? selected = tasks.FirstOrDefault(task => task.IsSelected && task.IsSelectable);
        var elements = new List<object>
        {
            Markdown((string.IsNullOrWhiteSpace(change) ? "" : $"✅ **{Escape(change)}**\n\n")
                + "订阅后，这个 Task 的新运行结果和需要你处理的人工门，都会自动推送到飞书。订阅不会改变当前 Task。"),
            Selector("subscription_project_selector", "筛选项目",
                (projects ?? [new(project, projectToken, true)]).Select(item => Option(item.Name, item.CapabilityToken)).ToArray(),
                projects?.SingleOrDefault(item => item.IsSelected)?.CapabilityToken ?? projectToken),
        };
        if (!string.IsNullOrWhiteSpace(membershipNotice)) elements.Add(Markdown(Escape(membershipNotice)));
        if (tasks.Any(task => task.IsSelectable))
        {
            elements.Add(Selector(
                "subscription_task_selector",
                "选择一个 Task",
                tasks.Where(task => task.IsSelectable).Select(task => Option($"{task.Title} · {task.Project}", task.CapabilityToken)).ToArray(),
                selected?.CapabilityToken));
            if (selected is not null)
            {
                elements.Add(Markdown($"**项目**：{Escape(selected.Project)}\n**Task**：{Escape(selected.Title)}"));
                elements.Add(Button("订阅这个 Task", "deepori.card.subscription.add", addToken, "primary_filled", selected.IsEnabled));
                elements.Add(Button("取消订阅这个 Task", "deepori.card.subscription.remove", removeToken, "default", !selected.IsEnabled));
            }
        }
        else
        {
            elements.Add(Markdown("当前授权项目没有可订阅的 Task。"));
        }
        if (tasks.Any(task => !task.IsSelectable))
            elements.Add(Markdown("**目录未授权的 Task（不可订阅）**\n" + string.Join("\n", tasks.Where(task => !task.IsSelectable)
                .Select(task => $"- {Escape(task.Title)} · {Escape(task.WorkingDirectory)}"))));
        elements.Add(Markdown($"**当前项目已订阅 {subscribedLabels.Count} 个 Task**\n"
            + (subscribedLabels.Count == 0 ? "当前项目尚未订阅任何 Task。" : string.Join("\n", subscribedLabels.Select(label => $"- {Escape(label)}")))));
        elements.Add(Button("刷新 Task 列表", "deepori.card.subscription.refresh", refreshToken));
        return Card("订阅桌面 Task", $"当前项目已订阅 {subscribedLabels.Count} 个", "blue", elements);
    }

    internal static string BuildDesktopSyncConfirmation(
        CodexThreadSummary task,
        string project,
        string resultStatus,
        string confirmToken,
        string otherToken,
        string cancelToken)
    {
        string status = resultStatus switch
        {
            "running" => "运行中；完成后自动推送",
            "completed" => "已有完成结果；接续后立即推送",
            "failed" => "最近一轮运行失败；接续后推送状态",
            "interrupted" => "最近一轮已停止；接续后推送状态",
            _ => "暂无完成结果",
        };
        return Card(
            "从 Codex Desktop 回到飞书",
            $"{Safe(project, 80)} · {Safe(task.Title, 80)}",
            "blue",
            [
                Markdown("**这是你在桥接中选择的当前 Task。**\n\n接续后，可以在飞书继续沟通；"
                    + "如果已有完成结果，会立即推送最近一条。完整历史仍保留在 Codex Desktop。"
                    + $"\n\n项目：**{Escape(project)}**\nTask：**{Escape(task.Title)}**\n桌面状态：**{status}**"
                    + "\n\n如果不是你要接续的 Task，请先选择「接续其他 Task」。"),
                Button("接续当前 Task", "deepori.card.sync.confirm", confirmToken, "primary_filled"),
                Button("接续其他 Task", "deepori.card.sync.other", otherToken),
                Button("暂不接续", "deepori.card.sync.cancel", cancelToken),
            ]);
    }

    internal static string BuildModelSettings(
        CodexThreadSummary task,
        string project,
        IReadOnlyList<FeishuModelOption> models,
        IReadOnlyList<FeishuModelOption> efforts,
        IReadOnlyList<FeishuModelOption> tiers,
        string refreshToken,
        string? change = null)
    {
        var elements = new List<object>
        {
            Markdown((string.IsNullOrWhiteSpace(change) ? "" : $"✅ **{Escape(change)}**\n\n")
                + $"**项目**：{Escape(project)}\n**Task**：{Escape(task.Title)}\n\n"
                + "模型、分析强度和速度仅作用于这个 Task，从下一条尚未开始的消息生效。正在运行的这一轮不会改变。"
                + $"\n\n**当前设置**\n模型：`{Escape(task.Model)}`"
                + (string.IsNullOrWhiteSpace(task.ReasoningEffort) ? "" : $"\n推理强度：`{Escape(task.ReasoningEffort)}`")
                + $"\n速度：`{Escape(task.ServiceTier)}`"),
        };
        if (models.Count > 0)
        {
            elements.Add(Selector("task_model_selector", "选择模型", models.Select(item => Option(item.Label, item.CapabilityToken)).ToArray(), models.FirstOrDefault(item => item.IsSelected)?.CapabilityToken));
        }
        if (efforts.Count > 0)
        {
            elements.Add(Selector("task_effort_selector", "选择推理强度", efforts.Select(item => Option(item.Label, item.CapabilityToken)).ToArray(), efforts.FirstOrDefault(item => item.IsSelected)?.CapabilityToken));
        }
        if (tiers.Count > 0)
        {
            elements.Add(Selector("task_speed_selector", "选择速度", tiers.Select(item => Option(item.Label, item.CapabilityToken)).ToArray(), tiers.FirstOrDefault(item => item.IsSelected)?.CapabilityToken));
        }
        elements.Add(Button("刷新当前设置", "deepori.card.model.refresh", refreshToken));
        return Card($"Task：{Safe(task.Title, 80)}", $"项目：{Safe(project, 80)}", "blue", elements);
    }

    private static object Selector(string name, string placeholder, IReadOnlyList<object> options, string? initial) => new
    {
        tag = "select_static",
        name,
        placeholder = new { tag = "plain_text", content = placeholder },
        options,
        initial_option = initial,
        width = "fill",
    };

    private static object Option(string label, string token) => new
    {
        text = new { tag = "plain_text", content = Safe(label, 100) },
        value = token,
    };

    private static object Button(string label, string action, string token, string type = "default", bool disabled = false) => new
    {
        tag = "button",
        text = new { tag = "plain_text", content = label },
        type,
        name = action,
        value = new { deepori_token = token },
        disabled,
        width = "fill",
    };

    private static object Markdown(string content) => new { tag = "markdown", content };

    private static string Card(string title, string subtitle, string template, IReadOnlyList<object> elements) => JsonSerializer.Serialize(new
    {
        schema = "2.0",
        config = new { update_multi = true, width_mode = "default", enable_forward = false, summary = new { content = title } },
        header = new
        {
            title = new { tag = "plain_text", content = title },
            subtitle = new { tag = "plain_text", content = subtitle },
            template,
            icon = new { tag = "standard_icon", token = "ai-common_colorful" },
        },
        body = new { direction = "vertical", padding = "12px 12px 20px 12px", vertical_spacing = "12px", elements },
    });

    private static string Safe(string? value, int max)
    {
        string normalized = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= max ? normalized : normalized[..(max - 1)] + "…";
    }

    private static string Escape(string? value)
    {
        string safe = Safe(value, 200);
        foreach (char character in "\\`*_{}[]()#+-.!|>") safe = safe.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        return safe;
    }
}
