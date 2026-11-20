using System.Text.Json;

namespace DeepOriBridge.Services;

internal sealed record FeishuTaskCardOption(
    string Title,
    string Project,
    string CapabilityToken,
    bool IsSelected,
    bool IsSelectable = true,
    string WorkingDirectory = "");

internal sealed record FeishuProjectCardOption(string Name, string CapabilityToken, bool IsSelected);

internal static class FeishuTaskSelectorCardBuilder
{
    internal static string Build(
        string project,
        string projectCapabilityToken,
        IReadOnlyList<FeishuTaskCardOption> tasks,
        string refreshCapabilityToken,
        bool selectionChanged)
        => Build([new FeishuProjectCardOption(project, projectCapabilityToken, true)],
            tasks, refreshCapabilityToken, selectionChanged);

    internal static string Build(
        IReadOnlyList<FeishuProjectCardOption> projects,
        IReadOnlyList<FeishuTaskCardOption> tasks,
        string refreshCapabilityToken,
        bool selectionChanged,
        bool choosingCurrent = false,
        string? membershipNotice = null)
    {
        FeishuTaskCardOption? selected = tasks.FirstOrDefault(task => task.IsSelected);
        string title = selected is null
            ? choosingCurrent ? "当前 Task · 请选择" : "切换 Codex Task"
            : $"Task：{SafeText(selected.Title, 80)}";
        string subtitle = selected is null
            ? "切换后，后续文字会发送到该 Task"
            : $"项目：{SafeText(selected.Project, 80)}";

        var header = new Dictionary<string, object?>
        {
            ["title"] = new { tag = "plain_text", content = title },
            ["subtitle"] = new { tag = "plain_text", content = subtitle },
            ["template"] = selected is null ? "blue" : "green",
            ["icon"] = new { tag = "standard_icon", token = "ai-common_colorful" },
        };
        if (selected is not null)
        {
            header["text_tag_list"] = new object[]
            {
                new
                {
                    tag = "text_tag",
                    text = new { tag = "plain_text", content = "当前 Task" },
                    color = "green",
                },
            };
        }

        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = selectionChanged
                    ? "✅ **当前 Task 已切换**\n后续消息会持续发送到这个 Task。"
                    : choosingCurrent
                    ? "请选择要继续的 Task。下方已列出授权项目的真实 Task；选中后立即显示当前状态，并记住你的选择。"
                    : "**切换 Codex Task**\n先选项目，再选 Task；当前 Task 会持续保留，直到你选择新的 Task。",
            },
        };
        if (!string.IsNullOrWhiteSpace(membershipNotice))
            elements.Add(new { tag = "markdown", content = MarkdownEscape(membershipNotice) });
        if (selected is not null)
        {
            elements.Add(new
            {
                tag = "markdown",
                content = $"**项目**：{MarkdownEscape(selected.Project)}\n**Task**：{MarkdownEscape(selected.Title)}",
            });
        }

        elements.Add(new
        {
            tag = "select_static",
            name = "project_selector",
            placeholder = new { tag = "plain_text", content = "筛选项目" },
            options = projects.Select(project => new
            {
                text = new { tag = "plain_text", content = SafeText(project.Name, 80) },
                value = project.CapabilityToken,
            }).ToArray(),
            initial_option = projects.SingleOrDefault(project => project.IsSelected)?.CapabilityToken,
            width = "fill",
        });

        if (tasks.Any(task => task.IsSelectable))
        {
            elements.Add(new
            {
                tag = "select_static",
                name = "task_selector",
                placeholder = new { tag = "plain_text", content = "切换到一个 Task" },
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
        }
        else
        {
            elements.Add(new
            {
                tag = "markdown",
                content = tasks.Count == 0
                    ? "这个项目暂时没有可读取的 Task。"
                    : "该项目的 Task 工作目录尚未授权，暂时不能切换。",
            });
        }
        if (tasks.Any(task => !task.IsSelectable))
        {
            elements.Add(new
            {
                tag = "markdown",
                content = "**目录未授权的 Task**\n" + string.Join("\n", tasks.Where(task => !task.IsSelectable)
                    .Select(task => $"• {MarkdownEscape(task.Title)} · 工作目录：{MarkdownEscape(task.WorkingDirectory)}")),
            });
        }

        elements.Add(new
        {
            tag = "markdown",
            content = $"<font color='grey'>{projects.Count} 个已授权本机项目 · 第 1/1 页 · {tasks.Count} 个 Task</font>",
        });
        elements.Add(new
        {
            tag = "button",
            text = new { tag = "plain_text", content = "刷新 Task 列表" },
            type = "default",
            name = "deepori.card.selector.refresh",
            value = new { deepori_token = refreshCapabilityToken },
        });

        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                update_multi = true,
                width_mode = "default",
                enable_forward = false,
            },
            header,
            body = new
            {
                direction = "vertical",
                padding = "12px 12px 20px 12px",
                vertical_spacing = "12px",
                elements,
            },
        });
    }

    private static string SafeText(string? value, int maxLength)
    {
        string normalized = string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
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
