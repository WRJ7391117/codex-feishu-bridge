using System.Globalization;
using System.Text.Json;

namespace DeepOriBridge.Services;

internal static class FeishuCurrentTaskCardBuilder
{
    internal static string ProjectStatus(
        string? codexThreadStatus,
        bool routeExecuting,
        bool routePending)
    {
        if (routeExecuting || IsRunning(codexThreadStatus))
        {
            return "运行中";
        }

        return routePending ? "等待执行" : "空闲";
    }

    internal static bool IsRunning(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

    internal static string Build(
        string title,
        string project,
        string status,
        int activeRuns,
        int queuedInputs,
        DateTimeOffset updatedAt,
        string refreshCapabilityToken,
        string switchCapabilityToken,
        string recentReply = "",
        string? membershipNotice = null)
    {
        string statusTagColor = activeRuns > 0
            ? "blue"
            : queuedInputs > 0
                ? "yellow"
                : "neutral";
        string statusTag = activeRuns > 0
            ? "运行中"
            : queuedInputs > 0
                ? "有排队"
                : "空闲";

        string details = $"**项目**：{MarkdownEscape(project)}\n**Task**：{MarkdownEscape(title)}\n\n"
            + $"**当前状态**\n{MarkdownEscape(status)}\n\n"
            + $"<font color='grey'>运行中：{activeRuns} · 排队：{queuedInputs} · 更新："
            + updatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            + "</font>";
        if (!string.IsNullOrWhiteSpace(membershipNotice))
            details += "\n\n" + MarkdownEscape(membershipNotice);
        if (!string.IsNullOrWhiteSpace(recentReply))
        {
            details += $"\n\n**最近回复**\n{MarkdownEscape(recentReply)}";
        }

        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                update_multi = true,
                width_mode = "default",
                enable_forward = false,
                summary = new { content = $"当前 Task：{SafeText(title, 100)}" },
            },
            header = new
            {
                title = new { tag = "plain_text", content = $"Task：{SafeText(title, 80)}" },
                subtitle = new { tag = "plain_text", content = $"项目：{SafeText(project, 80)}" },
                template = "green",
                icon = new { tag = "standard_icon", token = "ai-common_colorful" },
                text_tag_list = new object[]
                {
                    new
                    {
                        tag = "text_tag",
                        text = new { tag = "plain_text", content = "当前 Task" },
                        color = "green",
                    },
                    new
                    {
                        tag = "text_tag",
                        text = new { tag = "plain_text", content = statusTag },
                        color = statusTagColor,
                    },
                },
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
                        content = details,
                    },
                    Button("刷新状态", "deepori.card.current.refresh", refreshCapabilityToken),
                    Button("切换 Task", "deepori.card.selector.open", switchCapabilityToken),
                },
            },
        });
    }

    private static object Button(string label, string action, string token) => new
    {
        tag = "button",
        text = new { tag = "plain_text", content = label },
        type = "default",
        name = action,
        value = new { deepori_token = token },
    };

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
