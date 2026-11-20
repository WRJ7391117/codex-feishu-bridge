using System.Globalization;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed record FeishuPromLightTaskOption(
    string Title,
    string Project,
    string CapabilityToken,
    bool IsSelected,
    bool IsAssociated,
    bool IsSelectable = true,
    string WorkingDirectory = "");

internal static class FeishuPromLightCardBuilder
{
    internal const string LegendText =
        "绿灯常亮：已完成，当前无需处理\n"
        + "黄灯常亮：正在处理中\n"
        + "黄灯闪烁：需要你处理\n"
        + "红灯闪烁：执行出错，请查看 Task";

    internal static string StatusLabel(PromLightStatus status) => status switch
    {
        PromLightStatus.Idle => "绿灯常亮 · 已完成/空闲",
        PromLightStatus.Running => "黄灯常亮 · 正在处理中",
        PromLightStatus.HumanGate => "黄灯闪烁 · 需要你处理",
        PromLightStatus.Error => "红灯闪烁 · 执行出错",
        _ => "状态待确认",
    };

    internal static string BuildControl(
        PromLightDeviceSnapshot device,
        PromLightActivitySnapshot activity,
        PromLightBindingSnapshot binding,
        IReadOnlyList<string> associatedTaskLabels,
        string manageTasksCapabilityToken,
        string deviceSettingsCapabilityToken,
        string refreshCapabilityToken,
        string legendCapabilityToken)
    {
        bool online = device.Found
            && device.Opened
            && device.ReleaseNumber == PromLightProtocol.SupportedReleaseNumber;
        string name = SafeText(binding.Name, 40);
        string updated = activity.SubmittedAt is DateTimeOffset submittedAt
            ? submittedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "尚未更新";
        string delivery = activity.BytesWritten > 0
            ? "设备已 ACK，灯效未独立回读"
            : "尚未下发";
        string deviceDetail = online
            ? $"**{name}** · 默认灯\n"
                + "在线 · 当前中继：本地 Bridge\n"
                + $"最后逻辑状态：{StatusLabel(activity.Status)}\n"
                + $"状态更新时间：{updated}\n"
                + $"投递语义：{delivery}\n"
                + "关联 Task："
                + (associatedTaskLabels.Count > 0
                    ? string.Join("、", associatedTaskLabels.Select(MarkdownEscape))
                    : "未关联")
            : "**我的提示灯**\n提示灯离线。请在运行 Bridge 的 Windows App 首页检查设备连接。";

        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = "提示灯由运行 Bridge 的 Windows 电脑控制。\n"
                    + "- **提示灯关联哪些 Task**：决定哪些 Task 会改变灯光。\n"
                    + "- **提示灯设备设置**：管理默认灯或解除与 Bridge 的 Task 绑定。",
            },
            new { tag = "markdown", content = deviceDetail },
        };
        if (online)
        {
            elements.Add(Button(
                "提示灯关联哪些 Task",
                "deepori.card.promlight.tasks.open",
                manageTasksCapabilityToken,
                "primary_filled"));
            elements.Add(Button(
                "提示灯设备设置",
                "deepori.card.promlight.settings.open",
                deviceSettingsCapabilityToken));
        }
        elements.Add(Button(
            "刷新状态",
            "deepori.card.promlight.refresh",
            refreshCapabilityToken));
        elements.Add(Button(
            "灯光状态说明",
            "deepori.card.promlight.legend",
            legendCapabilityToken));

        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                update_multi = true,
                width_mode = "default",
                enable_forward = false,
                summary = new { content = $"提示灯控制中心 · {(online ? "在线" : "离线")}" },
            },
            header = new
            {
                title = new { tag = "plain_text", content = "提示灯控制中心" },
                subtitle = new { tag = "plain_text", content = $"我的提示灯 · {(online ? 1 : 0)} 盏" },
                template = "blue",
                icon = new { tag = "standard_icon", token = "lightbulb_colorful" },
            },
            body = new
            {
                direction = "vertical",
                padding = "12px 12px 20px 12px",
                vertical_spacing = "12px",
                elements,
            },
        });
    }

    internal static string BuildTaskManager(
        string lampName,
        string project,
        string projectCapabilityToken,
        IReadOnlyList<FeishuPromLightTaskOption> tasks,
        IReadOnlyList<string> associatedTaskLabels,
        string addCapabilityToken,
        string removeCapabilityToken,
        string backCapabilityToken,
        string legendCapabilityToken,
        string? change = null,
        IReadOnlyList<FeishuProjectCardOption>? projects = null,
        string? membershipNotice = null)
    {
        FeishuPromLightTaskOption? selected = tasks.FirstOrDefault(task => task.IsSelected && task.IsSelectable);
        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = (string.IsNullOrWhiteSpace(change)
                        ? string.Empty
                        : $"✅ **{MarkdownEscape(change)}**\n\n")
                    + $"正在管理：**{MarkdownEscape(lampName)}**\n"
                    + "只有这里显式关联的 Task 会影响这盏灯。保存时会再次校验项目权限和 Task 可见性。",
            },
        };
        if (!string.IsNullOrWhiteSpace(membershipNotice))
            elements.Add(new { tag = "markdown", content = MarkdownEscape(membershipNotice) });
        if (!string.IsNullOrWhiteSpace(project))
        {
            elements.Add(new
            {
                tag = "select_static",
                name = "promlight_project_selector",
                placeholder = new { tag = "plain_text", content = "选择项目" },
                options = (projects ?? [new(project, projectCapabilityToken, true)]).Select(item => new
                    {
                        text = new { tag = "plain_text", content = SafeText(item.Name, 80) },
                        value = item.CapabilityToken,
                    }).ToArray(),
                initial_option = projects?.SingleOrDefault(item => item.IsSelected)?.CapabilityToken ?? projectCapabilityToken,
                width = "fill",
            });
        }
        if (tasks.Any(task => task.IsSelectable))
        {
            elements.Add(new
            {
                tag = "markdown",
                content = $"**可选 Task：{tasks.Count(task => task.IsSelectable)} 个**\n点击下面的下拉框查看并选择。",
            });
            elements.Add(new
            {
                tag = "select_static",
                name = "promlight_task_selector",
                placeholder = new { tag = "plain_text", content = "选择 Task" },
                options = tasks.Where(task => task.IsSelectable).Select(task => new
                {
                    text = new
                    {
                        tag = "plain_text",
                        content = SafeText($"{task.Title} · {task.Project}", 100),
                    },
                    value = task.CapabilityToken,
                }).ToArray(),
                initial_option = selected?.CapabilityToken,
                width = "fill",
            });
            if (selected is not null)
            {
                elements.Add(new
                {
                    tag = "markdown",
                    content = $"**项目**：{MarkdownEscape(selected.Project)}\n"
                        + $"**Task**：{MarkdownEscape(selected.Title)}",
                });
                elements.Add(Button(
                    "关联这个 Task",
                    "deepori.card.promlight.tasks.add",
                    addCapabilityToken,
                    "primary_filled",
                    disabled: selected.IsAssociated));
                elements.Add(Button(
                    "取消关联这个 Task",
                    "deepori.card.promlight.tasks.remove",
                    removeCapabilityToken,
                    disabled: !selected.IsAssociated));
            }
        }
        else
        {
            elements.Add(new { tag = "markdown", content = "当前授权项目没有可关联的 Task。" });
        }

        if (tasks.Any(task => !task.IsSelectable))
            elements.Add(new { tag = "markdown", content = "**目录未授权的 Task（不可关联）**\n"
                + string.Join("\n", tasks.Where(task => !task.IsSelectable)
                    .Select(task => $"- {MarkdownEscape(task.Title)} · {MarkdownEscape(task.WorkingDirectory)}")) });
        elements.Add(new
        {
            tag = "markdown",
            content = $"**当前项目已关联 {associatedTaskLabels.Count} 个 Task**\n"
                + (associatedTaskLabels.Count > 0
                    ? string.Join("\n", associatedTaskLabels.Select(label => $"- {MarkdownEscape(label)}"))
                    : "当前项目尚未关联任何 Task。"),
        });
        elements.Add(Button("返回我的提示灯", "deepori.card.promlight.back", backCapabilityToken));
        elements.Add(Button("灯光状态说明", "deepori.card.promlight.legend", legendCapabilityToken));

        return BuildCard("关联 Task", lampName, elements);
    }

    internal static string BuildDeviceSettings(
        PromLightBindingSnapshot binding,
        string clearCapabilityToken,
        string backCapabilityToken) => BuildCard(
            "提示灯设置",
            binding.Name,
            new object[]
            {
                new
                {
                    tag = "markdown",
                    content = $"正在设置：**{MarkdownEscape(binding.Name)}** · 默认灯\n\n"
                        + "**解除与 Bridge 的绑定**会停止这盏灯的 Task 提醒并清除关联列表；"
                        + "不会断开 Windows 蓝牙，也不会改变设备名称。",
                },
                Button(
                    "解除与 Bridge 的绑定",
                    "deepori.card.promlight.settings.clear",
                    clearCapabilityToken,
                    "danger",
                    confirmTitle: "确认解除与 Bridge 的绑定？",
                    confirmText: "将停止这盏灯的 Task 提醒并清除关联列表。Windows 蓝牙连接不会被断开。"),
                Button("返回我的提示灯", "deepori.card.promlight.back", backCapabilityToken),
            });

    internal static string BuildLegend(string backCapabilityToken) => JsonSerializer.Serialize(new
    {
        schema = "2.0",
        config = new { update_multi = true, width_mode = "default", enable_forward = false },
        header = new
        {
            title = new { tag = "plain_text", content = "灯光状态说明" },
            subtitle = new { tag = "plain_text", content = "PromLight · 按 Task 状态显示" },
            template = "blue",
            icon = new { tag = "standard_icon", token = "lightbulb_colorful" },
        },
        body = new
        {
            direction = "vertical",
            padding = "12px 12px 20px 12px",
            vertical_spacing = "12px",
            elements = new object[]
            {
                new
                {
                    tag = "markdown",
                    content = "**完整说明**\n" + LegendText
                        + "\n\n**状态优先级**\n红灯闪烁 > 黄灯闪烁 > 黄灯常亮 > 绿灯常亮"
                        + "\n\n命令 ACK 不等于灯效已独立回读验证。",
                },
                Button("返回我的提示灯", "deepori.card.promlight.back", backCapabilityToken),
            },
        },
    });

    private static string BuildCard(
        string title,
        string subtitle,
        IReadOnlyList<object> elements) => JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new { update_multi = true, width_mode = "default", enable_forward = false },
            header = new
            {
                title = new { tag = "plain_text", content = SafeText(title, 80) },
                subtitle = new { tag = "plain_text", content = SafeText(subtitle, 80) },
                template = "blue",
                icon = new { tag = "standard_icon", token = "lightbulb_colorful" },
            },
            body = new
            {
                direction = "vertical",
                padding = "12px 12px 20px 12px",
                vertical_spacing = "12px",
                elements,
            },
        });

    private static object Button(
        string label,
        string action,
        string token,
        string type = "default",
        bool disabled = false,
        string? confirmTitle = null,
        string? confirmText = null)
    {
        var button = new Dictionary<string, object?>
        {
            ["tag"] = "button",
            ["text"] = new { tag = "plain_text", content = label },
            ["type"] = type,
            ["name"] = action,
            ["value"] = new { deepori_token = token },
            ["disabled"] = disabled,
        };
        if (!string.IsNullOrWhiteSpace(confirmTitle))
        {
            button["confirm"] = new
            {
                title = new { tag = "plain_text", content = confirmTitle },
                text = new { tag = "plain_text", content = confirmText ?? string.Empty },
            };
        }
        return button;
    }

    private static string SafeText(string? value, int maxLength)
    {
        string normalized = string.Join(
            ' ',
            (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string MarkdownEscape(string? value)
    {
        string safe = SafeText(value, 160);
        foreach (char character in "\\`*_{}[]()#+-.!|>")
        {
            safe = safe.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        }
        return safe;
    }
}
