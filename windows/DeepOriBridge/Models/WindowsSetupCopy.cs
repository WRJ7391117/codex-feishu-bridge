namespace DeepOriBridge.Models;

internal static class WindowsSetupCopy
{
    public const string CodexPrompt = """
        请使用 $deepori-bridge-setup 配置这台 Windows 电脑上的 DeepOri Bridge。

        安全阻断：在跨应用身份契约通过独立审计前，不得自动查找、打开、初始化或启动任何 DeepOri 应用。尤其禁止 Get-AppxPackage 后用 -match DeepOri、Select-Object -First 1 或窗口标题猜测目标；DeepOri.AgentMesh / DeepOriAgentMesh.exe / “初始化本机节点” / “启动本地服务”都不是 Bridge，遇到即停止。Bridge 候选必须同时精确匹配 DeepOriBridge.exe、产品名“DeepOri Bridge for Windows”和页面 AutomationId 前缀“DeepOriBridge.”；零个或多个候选都必须 fail-closed。

        请先只读检查，再尽可能自动完成配置。不要让我把 App Secret 发到 Codex 对话、终端参数、文件、状态或日志中；需要凭证时，请让我只在 DeepOri Bridge for Windows 的安全输入框中填写，由当前 Windows 用户的 Credential Locker 保存。

        仅在真正需要人工确认时暂停，并明确告诉我现在要做什么：
        1. 飞书登录、验证码或二次验证；
        2. 在 DeepOri Bridge 中输入 App ID 和 App Secret；
        3. 批准权限或发布飞书应用版本；
        4. 为 owner 选择明确的 Codex 项目授权。

        不要默认开放全部项目。当前 Codex 自动配置入口在 App 内保持禁用；只能说明手动步骤，不能执行初始化、启动服务、写状态、弹出变更确认或发布。完成后请验证的对象必须仍是同一个 Bridge 进程与同一个 App ID 指纹。
        """;

    public static IReadOnlyList<string> ConfigurationChecklist { get; } =
    [
        "添加机器人能力，并确认机器人可用范围符合预期。",
        "开启长连接事件订阅。",
        "三个长连接消费者均需配置并实际回调：im.message.receive_v1、application.bot.menu_v6、card.action.trigger。",
        "12 个菜单 Event Key：current_task、select_task、new_task、archive_task、task_settings、compact_task_context、codex_usage、sync_desktop、sync_desktop_switch、task_subscriptions、promlight、promlight_legend。",
        "一级菜单层级：Task（当前/选择/新建/归档/设置/压缩）、桌面接续（当前/其他/订阅）、Codex 用量、PromLight（设备/状态说明）。",
        "授予接收单聊消息、读取消息与消息资源所需权限。",
        "授予发送和更新消息、上传图片与文件（含 im:resource）所需权限。",
        "创建并发布应用版本；权限、事件与发布状态以飞书开放平台显示为准。",
        "不要默认开放全部 Codex 项目；只授权明确选择的项目。",
    ];
}
