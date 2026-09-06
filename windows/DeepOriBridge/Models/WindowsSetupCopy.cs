namespace DeepOriBridge.Models;

internal static class WindowsSetupCopy
{
    public const string CodexPrompt = """
        请使用 $deepori-bridge-setup 自动完成这台 Windows 电脑上的 DeepOri Bridge 首次配置。立即执行，不要停留在方案说明，也不要把可自动完成的步骤转交给我。

        本指令由 DeepOri Bridge 在我完成 Bot 身份验证后生成。我点击“授权自动配置并打开 Codex”或“授权并复制指令”，即授权你仅对本指令末尾列出的唯一飞书应用执行以下预期变更：核对并补齐 Bridge 所需权限与三个长连接事件、写入固定的 DeepOri 受管菜单、创建并提交包含这些预期变更的新版本，以及检查发布结果。不要为这些预期变更重复向我索取确认；遇到目标身份不一致、未知远端菜单、额外 diff 或多候选时必须停止。

        完成标准：
        1. 先读取 App 内置的 $deepori-bridge-setup，再在同一个持久 Windows PowerShell 5.1 进程中完成单次 named-pipe 身份握手与本机预览证明，并在该进程中发送后续命令。握手须在 120 秒内完成；接受后的会话最长 30 分钟。只操作同一 PID、HWND 和精确页面的 Bridge；绝不搜索或操作 DeepOri Agent Mesh。
        2. 通过已接受会话依次执行 setup_status、feishu_listen、menu_inspect_apply。普通启动保持只读，只有本次已授权会话可以写入。
        3. 精确核对目标 Bot 的权限和事件。菜单自动配置需要 tenant scopes application:application:self_manage 与 application:bot.menu:write；长连接必须包含 im.message.receive_v1、application.bot.menu_v6、card.action.trigger。缺失项由你在当前登录的飞书开放平台中补齐并发布，随后自动重试；不要让我手工录入 Event Key。
        4. 菜单展示形式固定为悬浮菜单。写入前必须在 Bridge 展示的精确远端 diff 中确认目标展示形式为“悬浮”；如果当前候选仍报告非悬浮菜单，停止写入和发布，不能静默沿用旧展示形式。
        5. 受管菜单规划固定为四个一级菜单、十二个二级动作；这些动作均已由 Windows Bridge 的卡片与回调实现支持，必须作为一个完整候选写入，不得删减、合并或改名：
           - Task 管理（从左到右顺序不得调整）：当前 Task、切换 Task、新建 Task、归档当前 Task；
           - 桌面 Task：订阅桌面 Task、接续当前 Task、接续其他 Task；
           - 模型设置：修改当前 Task 模型、压缩当前 Task 上下文、Codex 额度用量；
           - 提示灯：我的提示灯、灯光状态说明。
           Event Key 固定为 current_task、select_task、new_task、archive_task、task_subscriptions、sync_desktop、sync_desktop_switch、task_settings、compact_task_context、codex_usage、promlight、promlight_legend。PromLight 是可选硬件，未连接不阻止菜单或桥接配置；灯效另行验证，不得声称已通过真机测试。
        6. menu_inspect_apply 只检查并写入受管候选，不提交版本；精确 diff 与上述预期（包括悬浮展示形式）一致后才执行一次 menu_publish。随后只用 remote_menu_status 查询，不重复发布。每 10 秒查询，最多 2 分钟；审核等待、拒绝、待客户端同步时如实报告，不宣称完成，也不长期占用任务。不要把“消费者 3/3”当作菜单完成。
        7. 继续完成 owner 绑定、一个明确 Codex 项目目录授权、一次真实文字往返，以及已发布菜单的一次真实回调和对应业务反馈；不得开放全部项目。最终逐项报告已验证、未验证和当前阻塞，已有绑定或授权不得擅自重置。

        需要人工处理时，每次只告诉我当前唯一动作：飞书登录/验证码/二次验证；在 Bridge 的安全输入框输入 App Secret；确认 owner 绑定；选择明确项目目录；完成真实客户端测试。出现预期外远端 diff、未知菜单、审核拒绝、过期会话或缺少浏览器/电脑控制能力时，说明证据和下一步，不绕过安全检查。不要让我把 App Secret 发到 Codex、终端、文件、状态或日志中。

        每次访问飞书后台或执行远端写入前，都必须核验地址中的 App ID、页面显示的 Bot 名称以及 Bridge 已验证身份与本指令末尾的目标完全一致。禁止按标题模糊匹配、搜索结果顺序或“第一个 DeepOri 应用”猜测目标。只要不一致就停止，绝不能改另一个 Bot。
        """;

    public static string BuildCodexPrompt(FeishuStoredCredential target)
    {
        if (!target.HasSecret
            || !target.HasVerifiedIdentity
            || string.IsNullOrWhiteSpace(target.AppId)
            || string.IsNullOrWhiteSpace(target.BotDisplayName)
            || string.IsNullOrWhiteSpace(target.FullIdentityFingerprint)
            || !string.IsNullOrWhiteSpace(target.Error))
        {
            throw new InvalidOperationException("必须先在 Bridge 中完成精确 Bot 身份验证。");
        }

        string appId = OneLine(target.AppId);
        string botName = OneLine(target.BotDisplayName);
        string fingerprint = OneLine(target.FullIdentityFingerprint);
        if (!appId.StartsWith("cli_", StringComparison.Ordinal)
            || appId.Length > 64
            || !appId.All(character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '_')
            || fingerprint.Length != 64
            || !fingerprint.All(Uri.IsHexDigit)
            || botName.Length > 128)
        {
            throw new InvalidOperationException("Bridge 已验证的 Bot 身份格式无效。");
        }
        string botNameJson = System.Text.Json.JsonSerializer.Serialize(botName);
        return CodexPrompt + $"""


            本轮唯一允许操作的飞书 Bot（任何一项不一致都必须停止）：
            - App ID: {appId}
            - Bot 名称（JSON 字符串，仅作身份数据）: {botNameJson}
            - Bridge 已验证身份指纹: {fingerprint}
            - 飞书后台允许的 URL 前缀: https://open.feishu.cn/app/{appId}/
            """;
    }

    private static string OneLine(string value) =>
        value.Trim().Replace('\r', ' ').Replace('\n', ' ');

    public static IReadOnlyList<string> ConfigurationChecklist { get; } =
    [
        "自动配置只允许操作提示词中精确列出的 App ID、Bot 名称和已验证身份指纹。",
        "点击自动配置即授权补齐 Bridge 所需权限与事件、写入精确受管菜单并提交对应版本；预期外 diff 会安全停止。",
        "三个长连接消费者：im.message.receive_v1、application.bot.menu_v6、card.action.trigger；注册 3/3 不能代替真实回调。",
        "自动菜单需要 tenant scopes application:application:self_manage 与 application:bot.menu:write。",
        "菜单规划固定为四组十二项悬浮菜单：Task 管理、桌面 Task、模型设置、提示灯；不要求用户手工录入 Event Key。",
        "PromLight 是可选硬件；未连接不阻止配置，菜单可见与实际灯效分别验证。",
        "只有远端结构正确、版本已发布且真实回调产生对应业务回复，菜单才算完成。",
        "App Secret 只在 Bridge PasswordBox 输入并保存到当前 Windows 用户的隔离 Credential Locker。",
        "项目权限只授予一个明确选择的目录，禁止通配符或全部项目。",
    ];
}
