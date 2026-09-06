using System.Globalization;
using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal static class FeishuMacParityCardBuilder
{
    internal static string BuildArchiveTask(
        string title,
        string project,
        bool busy,
        IReadOnlyList<object> actions)
    {
        object[] tags = busy
            ? [CurrentTaskTag(), Tag("运行中", "yellow")]
            : [CurrentTaskTag()];
        string status = busy
            ? $"当前 Task：**{MarkdownEscape(title)} · {MarkdownEscape(project)}**\n\n"
                + "这个 Task 正在运行，完成或停止后才能归档。"
            : $"当前 Task：**{MarkdownEscape(title)} · {MarkdownEscape(project)}**\n\n"
                + "归档后会从未归档列表移除，可在 Codex Desktop 中恢复。";
        var elements = new List<object> { Markdown(status) };
        elements.AddRange(actions);
        return BuildCard(
            $"Task：{SafeText(title, 80)}",
            $"项目：{SafeText(project, 80)}",
            busy ? "yellow" : "red",
            tags,
            elements);
    }

    internal static string BuildDesktopSync(
        string title,
        string project,
        string status,
        IReadOnlyList<object> actions)
    {
        bool running = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        string statusText = running ? "运行中" : "已接续";
        string statusColor = running ? "blue" : "green";
        var elements = new List<object>
        {
            Markdown(
                $"**项目**：{MarkdownEscape(project)}\n**Task**：{MarkdownEscape(title)}\n\n"
                + "Windows Bridge 已接续这个 Codex Desktop Task。后续飞书消息会继续进入该 Task；"
                + "需要改用其他 Task 时，请使用下方按钮。"),
        };
        elements.AddRange(actions);
        return BuildCard(
            "从 Codex Desktop 回到飞书",
            $"{SafeText(project, 80)} · {SafeText(title, 80)}",
            running ? "blue" : "green",
            [CurrentTaskTag(), Tag(statusText, statusColor)],
            elements);
    }

    internal static string BuildCompactTaskContext(
        string title,
        string project,
        IReadOnlyList<object> actions)
    {
        var elements = new List<object>
        {
            Markdown(
                $"**项目**：{MarkdownEscape(project)}\n**Task**：{MarkdownEscape(title)}\n\n"
                + "Codex 会总结较早内容并保留关键上下文，让当前 Task 可以继续交流。\n\n"
                + "此操作不能撤销；Task 正在运行时不能执行。"),
        };
        elements.AddRange(actions);
        return BuildCard(
            $"Task：{SafeText(title, 80)}",
            $"项目：{SafeText(project, 80)}",
            "blue",
            [CurrentTaskTag(), Tag("上下文压缩", "blue")],
            elements);
    }

    internal static string BuildUsage(
        CodexUsageSnapshot usage,
        IReadOnlyList<object> actions)
    {
        var lines = new List<string>();
        if (usage.Availability == CodexUsageAvailability.Loading)
        {
            lines.Add("⏳ **正在读取本机 Codex 额度…**");
        }
        else if (usage.Availability == CodexUsageAvailability.Stale)
        {
            lines.Add("⚠️ **最新刷新失败，下面显示上次成功读取的数据。**");
        }

        foreach (CodexUsageBucket bucket in usage.Buckets)
        {
            foreach (CodexUsageWindow window in bucket.Windows)
            {
                string plan = string.IsNullOrWhiteSpace(bucket.PlanType)
                    ? bucket.LimitName
                    : bucket.PlanType;
                string reset = window.ResetsAt is DateTimeOffset resetsAt
                    ? resetsAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 重置"
                    : "重置时间未知";
                lines.Add(
                    $"**{MarkdownEscape(plan)} · {MarkdownEscape(window.Label)}："
                    + $"剩余 {window.RemainingPercent:0.#}% · {reset}**");
            }
        }

        if (usage.Buckets.Count == 0)
        {
            lines.Add("暂时没有额度数据，请点击下方按钮刷新。");
        }
        if (usage.UpdatedAt is DateTimeOffset updatedAt)
        {
            lines.Add(
                "<font color='grey'>数据更新："
                + updatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "</font>");
        }

        var elements = new List<object> { Markdown(string.Join("\n\n", lines)) };
        elements.AddRange(actions);
        return BuildCard(
            "Codex 用量",
            "本机 Codex 账号的实时额度",
            "blue",
            [],
            elements,
            summary: "Codex 用量");
    }

    internal static string BuildNotice(
        string title,
        string subtitle,
        string message,
        IReadOnlyList<object> actions,
        string template = "blue")
    {
        var elements = new List<object> { Markdown(message) };
        elements.AddRange(actions);
        return BuildCard(title, subtitle, template, [], elements);
    }

    private static string BuildCard(
        string title,
        string subtitle,
        string template,
        IReadOnlyList<object> tags,
        IReadOnlyList<object> elements,
        string? summary = null)
    {
        var header = new Dictionary<string, object?>
        {
            ["title"] = new { tag = "plain_text", content = title },
            ["subtitle"] = new { tag = "plain_text", content = subtitle },
            ["template"] = template,
            ["icon"] = new { tag = "standard_icon", token = "ai-common_colorful" },
        };
        if (tags.Count > 0)
        {
            header["text_tag_list"] = tags;
        }

        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                update_multi = true,
                width_mode = "default",
                enable_forward = false,
                summary = new { content = summary ?? SafeText(title, 100) },
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

    private static object Markdown(string content) => new { tag = "markdown", content };

    private static object CurrentTaskTag() => Tag("当前 Task", "green");

    private static object Tag(string text, string color) => new
    {
        tag = "text_tag",
        text = new { tag = "plain_text", content = text },
        color,
    };

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
        string safe = SafeText(value, 200);
        foreach (char character in "\\`*_{}[]()#+-.!|>")
        {
            safe = safe.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        }
        return safe;
    }
}
