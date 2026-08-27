# Codex 飞书桥接

把飞书 Bot 变成 Codex Desktop 的移动入口：在飞书里按项目选择桌面版左侧栏中的 Task，发送文字、图片、文件或音频，并接收可更新的运行状态和最终结果。

版本说明：当前源码和本机构建为 `1.9.10 (build 46)`；最新公开 Release 为 `1.9.4 (build 40)`。不得用旧版本或同版本不同构建覆盖。

面向其他 macOS 用户的 BYOA 产品边界和 2.0 验收标准见 [`docs/PRODUCTIZATION.md`](docs/PRODUCTIZATION.md)，当前已验证与待实机验证范围见 [`docs/TEST_MATRIX.md`](docs/TEST_MATRIX.md)。

## 能力

- 飞书机器人一级菜单为 `TASK`、“接续桌面 Task”和“额度用量”；`TASK` 下提供“当前 Task”“切换 Task”“新建 Task”“归档当前 Task”四个二级入口，“接续桌面 Task”下提供“接续当前 Task”“接续其他 Task”两个二级入口
- “接续桌面 Task”只读取该飞书用户在桥接中选择的当前 Task，并先显示项目、Task 标题和桌面状态供确认；没有有效当前 Task 时只打开选择卡，不自动猜测其他 Task
- 按 `项目 · Task 标题` 显示所有未归档本地 Task，每页 50 条，可翻页和按标题搜索
- 选中后持续保持当前 Task；并行 Task 返回最终结果时，当前 Task 自动跟随最后送达的结果，便于直接继续追问
- 运行、排队、授权和结果卡片按真实关系显示“当前 Task”“运行 Task”“排队 Task”或“结果所属 Task”，避免切换后旧卡误称当前
- Desktop 尚未加载目标 Task 时，桥接会通过 Desktop 官方 deep link 自动激活一次、恢复原前台 App 后再沿 IPC 提交；只有激活失败才显示“重试 Desktop”“使用备用 CLI”或取消，备用 CLI 仍需用户主动确认
- Desktop 实时同步只订阅当前 turn 的状态变化，不再回放完整历史，避免同一条飞书消息在桌面界面重复显示
- 每位用户有一张持续更新的当前状态卡，集中显示项目、Task、运行/排队状态、最近提问、最近回复和完成时间
- Mac App 显示 Codex 官方接口返回的剩余额度与重置时间；飞书“额度用量”入口打开的卡片提供实时用量、当日 Task 用量分析和当期 Task 用量分析，不占用当前 Task 状态卡
- Task 卡可切换“全部 Task / 最近使用 / 我的收藏”，收藏和最近记录按飞书用户独立保存
- 指定飞书用户白名单，每位用户独立保持自己的当前 Task
- 按 Codex 项目限制每位用户可查看和提交的 Task
- 未授权用户可在 Bot 单聊中自助提交申请，由 Mac 机主在 App 中分配明确项目后生效
- 可从独立卡片选择项目后新建 Task，也可在卡片中明确取消并退出新建流程
- 可在“切换 Task”卡片中查看已归档 Task，恢复后自动设为当前 Task
- 归档成功后可立即撤销归档、切换到其他 Task 或新建 Task
- 将飞书文字、图片、文档、代码文件和音频提交给同一个 Codex Desktop Task
- 支持单张图片消息和富文本中的多张图片，默认每轮最多 4 张、单张 20 MB
- 使用同一张进度卡持续更新“读取附件、运行、使用工具、整理回复、等待授权、完成”等阶段
- 同一 Task 运行中收到的新消息会按顺序排队；不同 Task 可并行，排队卡会区分同 Task 串行、Desktop 忙或全局并发已满
- 可在进度卡中停止当前运行；停止结果会区分“已确认”和“未确认”
- Codex 请求命令、文件修改或临时权限时，可在飞书卡片中“允许一次”或“拒绝”
- 完成后先回复文字结果，再逐张回复 Codex 生成或引用的图片
- Codex 明确链接的 PDF、Office、文本和代码结果文件会自动作为飞书附件返回
- 最终文字、结果图片、结果文件和进度卡发送失败都会持久化；网络恢复后补发而不重跑 Codex
- 为经批准的本机自动化工作流主动发送里程碑卡片或人工决策卡；普通过程和通用 turn-complete 不发送
- workflow 通知先写入本机持久 outbox，断网后按原幂等事件自动补发；人工决策只消费一次
- 标准 macOS 主窗口集中显示事件消费者、运行 Task、排队消息、待补发、最近事件、配置、诊断和日志入口，并可设置 1～8 个并发 Task
- 菜单栏保留随时开启、关闭和快速打开控制中心的入口
- 用户级 LaunchAgent 常驻，异常退出后自动重启
- App 内置修复卡片回执的专用 `lark-cli`，避免项目或 Task 已选中但飞书仍提示 `108002`
- App 启动时自动检查 GitHub Releases，可在控制中心校验 SHA-256、App 身份、签名和 Universal 架构后自动替换；存在待处理飞书工作时拒绝升级
- 支持 Apple Silicon 和 Intel Mac

## 原理

```text
飞书文字 / 图片 / 文件 / 音频 / 卡片 / Bot 菜单
            ↓ WebSocket 长连接
   App 内置 lark-cli
            ↓ NDJSON 事件
     本地 bridge.py
       ├─ 读取 Codex Desktop 本地 Task 目录
       ├─ 检查“飞书用户 → 允许项目”权限
       ├─ 保存“飞书用户 → 当前 Task”状态
       ├─ 持久跟踪“飞书用户 → 桌面运行结果”订阅
       ├─ 读取 Codex 官方实时额度并仅向所有者展示
       ├─ 将飞书附件下载到本轮受限临时目录
       ├─ 后台运行并原地更新进度卡
       └─ 通过 Codex Desktop IPC 继续同一个 Task
                         ↓
                  Codex 执行与输出
                         ↓
       lark-cli 更新进度卡并回复文字、图片、文件结果
                         ↓
                       飞书
```

这里的 Task 就是 Codex Desktop 左侧栏中的一条对话。项目是 Task 的归类；每次发送消息或附件只是该 Task 内的一轮，不会新建 Task。

桥接在 Mac 本机运行，因此 Mac 必须开机、联网，Codex Desktop 的本地数据必须可用。飞书 Bot 本身不运行 Codex。

提交时优先使用 Codex Desktop IPC。若 Desktop 返回 `no-client-found`，桥接会通过受支持的 `codex://threads/<Task UUID>` 入口自动加载一次目标 Task，短暂等待 owner 建立并把焦点还给原来的前台 App，然后只沿原 IPC 通道重试；已经发出但确认状态不明的请求绝不会自动重放。自动激活失败时才进入人工选择。桥接只会在备用 Codex CLI 与该 Task 的本地记录版本兼容且用户明确确认时回退；默认优先使用 Desktop App 内置 CLI，而不是 PATH 中可能过旧的全局 CLI。版本无法确认或不兼容时，当前 Task 选择不会丢失。

飞书消息运行期间，Codex Desktop 仍会提示该 Task“已在另一个应用中打开”，以避免两个客户端同时写入；桥接会主动加载完整历史，因此桌面版可在提示下方只读查看已有内容和实时更新。飞书运行完成后，桌面版恢复可输入状态。

### 可选私有扩展：Ori One workflow 通知

这一扩展不属于通用桥接的产品承诺，不出现在首次连接向导中，并且默认关闭。它保留给明确需要 Ori One 自动研发通知的私有部署。安装后使用专用 `workflow-config` 配置，不要用通用 `jq`、命令参数或日志处理含本机标识的 `config.json`。`--enable` 只从 stdin 读取置顶专用 Codex Task UUID，自动选择已经同时存在于 legacy sender 和用户白名单中的本机用户；它不会猜测 Chat。Chat 留空时，以首张卡实际返回并写入持久 outbox 的 Chat 关联为准。

```bash
support="$HOME/Library/Application Support/Codex Feishu Bridge"
"$support/workflow-config" --status
read -r workflow_task_id
printf '%s\n' "$workflow_task_id" | "$support/workflow-config" --enable
unset workflow_task_id
```

需要关闭时单独运行：

```bash
"$support/workflow-config" --disable
```

配置工具只输出 `configured`、`disabled` 或 `invalid`，不输出用户、Chat 或 Task 标识。`--disable` 只写入关闭状态，可保留已经存在的本机绑定。桥接进程只在启动时读取配置，因此启用或关闭后都要从 App 控制中心重启一次后台桥接，状态才会生效。

安装后的入口及只读/受控操作：

```bash
support="$HOME/Library/Application Support/Codex Feishu Bridge"
"$support/workflow-notify" --health
"$support/workflow-notify" --status
"$support/workflow-notify" --dry-run < event.json
"$support/workflow-notify" --roundtrip-test
"$support/workflow-notify" --retry-outbox
"$support/workflow-notify" < event.json
```

通知 JSON 必须且只能包含以下七个字段；`status` 只允许 `milestone_completed` 或 `user_action_required`：

```json
{
  "workflow_id": "ori-one-mind",
  "event_id": "ONE-G1-102-completed-r1",
  "task_id": "ONE-G1-102",
  "status": "milestone_completed",
  "summary": "确定性检查、独立审查、提交和部署均已完成。",
  "workbench_url": "https://deepori.cn/ori-one/workbench/automation/",
  "actions": []
}
```

`workflow_id` 固定为 `ori-one-mind`。`workbench_url` 只允许 `https://deepori.cn/ori-one/workbench/automation/` 及其安全子路径，不接受本地地址、其他域名、端口、查询参数或锚点。

`milestone_completed` 的 `actions` 必须为空。`user_action_required` 必须带 2–5 个 action，每个 action 只能包含 `id / label / description / recommended / resolution`，且恰好一个推荐项；`resolution` 只允许 `resume / pause / stop`。`--dry-run` 只验证、不入队、不发送；payload 中出现 recipient、Chat 字段或其他额外字段会返回退出码 `2`。

CLI 退出码：`0` 表示验证通过或本机桥已接受请求；`1` 表示桥不可用或运行态操作失败；`2` 表示参数或 payload 无效。`--status` 只返回待发通知、待决策、待提醒和待恢复数量，不输出 workflow、任务、用户或 Chat 标识。

人工决策卡的按钮携带随机单次令牌；桥接器还会校验本机配置的用户和 Chat。有效响应先原子落盘，只安排一次卡片“已处理”更新，最后通过 Desktop IPC 恢复固定的专用 Codex Task；后续重复 callback 仅返回幂等确认。用户也可以直接回复该卡片的选项序号、ID 或完整名称。真实人工门恢复消息明确携带 `attention_request_id / selected_action_id / selected_action_label / resolution`，要求 Task 首先执行编排器的 `resolve-attention`，成功后才按检查点继续。

`--roundtrip-test` 会生成唯一的 `TEST-ROUNDTRIP` 安全测试卡。用户选择后，固定 Task 只收到一次测试 receipt；这条专用分支不读取或修改仓库、不调用 Neon 或 `resolve-attention`，也不租用或推进任何 `ONE-*` 任务。该命令会真实发卡，只能在用户在场、已明确同意端到端验证时运行。

Task 忙碌或 Desktop 暂不可用时，恢复消息按 `created_at` 留在严格持久 FIFO 中；提交确认不确定时标记为 `delivery_unknown`。未知恢复不会阻塞无关的初始通知；桥接器只从专用 Task 的用户输入记录核对相同 request/action/resolution 是否已经写入，确认后结清，没有证据时保持未知状态，绝不盲目重发。24 小时未处理提醒由桥接器唯一负责且只发送一次，Neon 和编排器不得再生成第二条提醒。

## 安装

### 1. 前置条件

- macOS 13 或更新版本
- 已安装并使用 Codex Desktop / ChatGPT Desktop
- 一个启用了机器人能力的飞书自建应用

App 已内置桥接所需的专用 `lark-cli`；普通用户无需先安装命令行工具。

### 2. 下载 App

从 [Releases](https://github.com/WRJ7391117/codex-feishu-bridge/releases) 下载 `Codex-Feishu-Bridge-macOS-universal.zip`，解压后把 `Codex 飞书桥接.app` 拖入“应用程序”。

若 Release 标注为 ad-hoc 签名且未经过 Apple 公证，另一台 Mac 首次打开时请在 Finder 中右键 App →“打开”。不要用脚本移除系统隔离属性。构建流程已支持 Developer ID 签名与 Apple 公证，是否已签名以对应 Release 说明和 `codesign` 验证结果为准。

### 3. 使用首次连接向导

首次打开 App 后点击“首次连接向导”，填写自己的飞书 App ID 和 App Secret。Secret 只通过 stdin 交给内置 `lark-cli`，由它存入 macOS Keychain，不会进入进程参数、桥接 `config.json` 或日志。

需要用终端维护时，才使用系统 `lark-cli`：

```bash
lark-cli --profile codex-notify doctor
```

App 运行时优先使用随安装包提供的 `1.0.89-codex-feishu.3`，它在官方 `v1.0.89` 基础上修复 `card.action.trigger` 的 WebSocket 回执类型、同步显示“正在处理…”反馈，并在 Ori One 工作流决策回执前写入本机耐久 inbox；Profile 和 Keychain 凭据仍与系统 `lark-cli` 共用。通用桥接可用 `lark_cli_path` 覆盖，但启用 workflow 时必须使用该内置版本，否则健康检查会 fail closed。

### 4. 配置飞书开发者后台

在飞书开放平台中完成：

1. 启用机器人能力，并把应用发布到当前租户。
2. 开通消息收发所需权限；至少包括接收单聊消息、读取消息及消息资源、发送/更新消息和上传结果图片（`im:resource`）。文件与音频输入同样依赖读取消息资源。可根据 lark-cli 返回的 `missing_scopes` 精确补充。
3. 在“事件与回调”中启用长连接，并订阅：
   - `im.message.receive_v1`
   - `application.bot.menu_v6`
4. 在“回调配置”中启用卡片回调 `card.action.trigger`。只启动监听但未配置这里时，卡片选择不会产生事件。
5. 在机器人菜单中配置三个主菜单：
   - 主菜单 `TASK` 下添加四个子菜单，动作均选择“推送事件”：
     - “当前 Task” → Event Key `current_task`
     - “切换 Task” → Event Key `select_task`
     - “新建 Task” → Event Key `new_task`
     - “归档当前 Task” → Event Key `archive_task`
   - 主菜单“额度用量”直接选择“推送事件” → Event Key `codex_usage`
   - 主菜单“接续桌面 Task”下添加两个子菜单，动作均选择“推送事件”：
     - “接续当前 Task” → Event Key `sync_desktop`
     - “接续其他 Task” → Event Key `sync_desktop_switch`
6. 创建并发布新的飞书应用版本；发布成功后菜单可能需要约 5 分钟生效。机器人菜单仅在与 Bot 的单聊中显示。

首次连接向导可启动一次两分钟监听。让机主给 Bot 发送一条单聊消息后，App 会自动识别 `open_id`，但不会自动授予任何项目。终端备用方式如下：

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

### 5. 配置并开启桥接

打开 `Codex 飞书桥接.app`，主控制窗口会自动出现：

1. 完成“首次连接向导”的 Profile 检查和首位用户识别；
2. 在“授权用户”中填写备注名，并从 Codex Desktop 左侧栏读取的项目列表中明确选择允许项目；
   - `*` 表示允许访问全部项目；
   - 多个项目用逗号分隔，名称必须与 Codex Desktop 左侧栏完全一致；
   - 点击“添加用户”可继续添加白名单用户；
3. 群 Chat ID 可留空，先使用与 Bot 的单聊；
4. 点击“开启桥接”；
5. 确认 App 中的七个 Event Key 与飞书后台完全一致，然后点击飞书 Bot 菜单“接续桌面 Task”做一次桌面结果同步测试。

未授权用户也可以先私聊 Bot 发任意消息。Bot 只会登记访问申请，不会开放任何 Task；机主在 App 的“待审批访问申请”中点击“配置授权”，填写明确项目并保存后才会生效。

关闭主窗口不会关闭后台桥接。再次点击 Dock 中的 App，或点击菜单栏双向箭头 →“打开控制中心”，即可重新打开窗口。

### 6. 卸载或恢复

控制中心的“移除后台服务…”会先确认队列为空，再停止 LaunchAgent 和事件总线、移除运行组件，同时保留飞书 Profile、授权配置、Task 状态和日志。以后重新打开 App 即可恢复安装；要移除 App 本身，再把它移到废纸篓。

只有确认要同时清除本机 Profile、配置、状态和日志时，才运行完整清除；命令会要求在 stdin 中再次输入 `PURGE`：

```bash
"/Applications/Codex 飞书桥接.app/Contents/Resources/bridge/uninstall.sh" --purge
```

## 飞书里的日常操作

- 点击 Bot 菜单 `TASK` →“当前 Task” → 在聊天底部打开最新状态卡；仅在尚未选择 Task 时进入选择页面
- 点击 Bot 菜单 `TASK` →“切换 Task” → 卡片只负责先选择项目、再从完整列表中切换到 `项目 · Task 标题`；取消切换时保持原 Task
- 点击 Bot 菜单“接续桌面 Task”→“接续当前 Task” → 卡片显示本人的桥接当前 Task、所属项目和桌面状态；确认“接续这个 Task”后，已完成结果立即推送，运行中的结果跨桥接重启持续跟踪并在完成后推送
- 点击 Bot 菜单“接续桌面 Task”→“接续其他 Task” → 先切换 Task，再进入该 Task 的接续确认卡；没有有效当前 Task 时“接续当前 Task”也会进入同一选择流程，不会自动猜测其他 Task
- 点击 Bot 菜单“额度用量” → 打开独立用量卡；卡片内可继续选择“当日 Task 用量分析”或“当期 Task 用量分析”，查看有权访问项目中的 Task Token 排名、占比、单次模型调用用量、主要消耗原因及是否偏高。“当日”从本地当天 00:00 开始，“当期”与当前 Codex 主额度重置周期一致；Token 用于 Task 间比较和异常诊断，不等同于官方额度或账单的按 Task 扣减
- 多个 Task 并行时，运行进度不会改变当前 Task；某个 Task 完成、失败或停止并返回最终内容后，当前 Task 自动跟随该结果。下一条消息会直接进入刚返回结果的 Task；随后另一个 Task 再返回时，当前 Task继续跟随最新结果
- 在 Task 卡的“显示范围”中切换“全部 Task / 最近使用 / 我的收藏”；可收藏或取消收藏当前 Task
- 项目 Task 超过 50 条时点击“上一页/下一页”；发送“搜索 关键词”可按标题筛选，卡片中可一键清除搜索
- 点击 Bot 菜单 `TASK` →“新建 Task” → 在独立卡片中选择项目并确认 → 发送 Task 标题 → Bot 创建并自动选择该 Task；点击“取消新建”可随时退出
- 点击 Bot 菜单 `TASK` →“归档当前 Task” → 独立卡片显示当前 Task → 可取消或二次确认归档
- 归档成功卡片可“撤销归档”“切换到其他 Task”或“新建 Task”
- 点击 Bot 菜单 `TASK` →“切换 Task” →“查看已归档 Task”→ 选择并确认恢复；恢复后自动成为当前 Task
- 选中一次后直接发送文字 → 始终继续当前 Task
- 直接发送一张图片 → 图片进入当前 Task；没有附带文字时会使用中性的图片提示
- 富文本消息可同时包含文字和多张图片
- 直接发送 PDF、Office 文档、文本、代码文件或音频 → 附件进入当前 Task；默认每轮最多 4 个、单个 50 MB
- 运行期间查看同一张进度卡；需要时点击“停止运行”
- Task 运行中继续发送消息 → Bot 显示排队位置，当前运行完成后自动执行；可点击“取消排队…”
- 收到授权卡片时，核对请求说明后点击“允许一次”或“拒绝”
- Codex 返回图片 → Bot 先回复文字，再把图片回复在同一条消息下
- Codex 返回本机 PDF、Office、文本或代码文件链接 → Bot 把文件作为附件回复在同一条消息下
- 再次点击“切换 Task” → 切换到另一个 Task，或点击“取消切换”保留当前 Task
- 发送“当前” → 查看当前 Task
- 发送“对话” → 在消息中重新打开 Task 卡片
- 发送“帮助” → 查看备用文字命令

## 本地文件

```text
/Applications/Codex 飞书桥接.app
~/Library/Application Support/Codex Feishu Bridge/
├── bridge.py
├── config.json
├── control.sh
├── diagnose.sh
├── workflow-config
├── workflow-notify
└── workflow_notifications.py
~/Library/LaunchAgents/com.deepori.codex-feishu-bridge.plist
~/.codex/feishu-bridge/state.json
~/.codex/feishu-bridge/workflow-state.json
~/.codex/feishu-bridge/workflow-decision-inbox/
~/.codex/feishu-bridge/workflow-notifications.sock
~/.codex/feishu-bridge/workflow-control.sock
~/.codex/log/feishu-bridge.log
```

`config.json` 保存 Profile 名、用户白名单、项目权限和本机 workflow 映射，不保存 App Secret。`state.json` 按用户分别保存当前 Task、收藏/最近记录、最近对话摘要、桌面结果订阅、访问申请、搜索/分页状态、待执行输入队列，以及尚未送达飞书的最终文字、图片、文件或卡片。本地待补发图片和文件分别复制到 `~/.codex/feishu-bridge/reply-images/` 与 `reply-files/`，送达后自动删除。`workflow-state.json` 保存主动通知 outbox、单次决策与恢复状态；`workflow-decision-inbox/` 在飞书 ACK 前以逐事件 `0600` 文件暂存 workflow callback，业务副作用完成后删除。队列和桌面结果订阅跨桥接重启保留；配置、状态、inbox、socket 与日志都限制为仅当前 macOS 用户可访问。

## 从源码构建

需要 Xcode Command Line Tools、Go 和网络连接。构建脚本会校验官方 `lark-cli v1.0.89` 的固定提交、应用仓库中的最小补丁、运行对应 Go 单测，并生成 Apple Silicon + Intel 通用二进制：

```bash
./scripts/build-app.sh
./scripts/install-local.sh
```

使用 Developer ID 和 Keychain 中的 notarytool Profile 构建正式公证包：

```bash
CODE_SIGN_IDENTITY="Developer ID Application: Example (TEAMID)" \
NOTARY_PROFILE="codex-feishu-notary" \
./scripts/build-app.sh
```

没有这两个凭证时构建脚本只生成 ad-hoc 包，不会伪装成已公证。每次构建同时生成 `.sha256` 和 `update.json`。

构建产物：

- `build/Codex 飞书桥接.app`
- `dist/Codex-Feishu-Bridge-macOS-universal.zip`

## 安全边界

- 默认只接受配置白名单中的飞书 `open_id`，不会自动开放给同租户其他用户
- 每位用户只能查看和提交其 `allowed_projects` 中的项目；列表展示和提交前都会校验
- 每位用户的当前 Task 独立保存；权限被移除后，原 Task 选择自动失效
- 群聊必须显式列入允许列表，或先由同一用户通过受信任卡片建立状态
- 消息按飞书 `message_id` 去重，回复使用幂等键
- 最终文字回复失败时会记录脱敏原因并进入持久化补发队列；网络恢复后使用原幂等键自动补发
- workflow 通知入口使用 `0600` Unix Socket；payload 不能包含收件人或 Chat 标识，目标只能来自 `0600` 本机配置
- 主动通知只允许里程碑完成与需要用户处理两类；通用 turn-complete 通知保持关闭
- workflow/event ID 在持久状态中幂等；相同 ID 的不同 payload 会被拒绝，断网补发不重新触发研发任务
- 人工决策同时校验配置用户、Chat、事件和随机令牌；成功消费后令牌即删除，只建立一条固定 Task 恢复记录
- 重复卡片 callback 不会再次消费决定或再次安排完成卡 patch；`TEST-ROUNDTRIP` 只向专用 Task 写入测试回执
- `delivery_unknown` 只读核对专用 Task 的 request/action/resolution 证据；无证据不重复提交
- 24 小时人工门提醒只由本机桥接器生成一次，Neon 与调用方不得另行提醒
- workflow 状态文件只接受当前用户拥有的 `0600` 普通文件；损坏 JSON、schema 漂移、符号链接或权限异常都会拒绝入口，绝不按空 outbox 覆盖
- workflow 卡片与 Task 提示中的可见文字会在入队前拒绝明显凭据、数据库连接串、私钥及飞书用户/Chat 标识
- 安装器在复制任何运行文件前预检全部安装资源、内置 CLI、配置、状态和日志；运行文件先完整暂存，替换失败时回滚，避免半升级
- 排队卡或进度卡更新失败时会保存待补发状态；同一张进度卡只保留最新版本，网络恢复后自动更新，不会重新执行 Codex
- 如果排队消息已经开始执行，尚未送达的旧“已排队”卡会被丢弃，避免恢复后显示过期状态
- 提交结果不确定时不会自动重复执行，避免同一任务运行两次
- 输入图片、文件和音频只从当前飞书消息的资源键下载，限制在本轮临时目录，并在回复完成后删除
- 默认每轮最多输入 4 张图片、单张 20 MB，只接受实际内容为 PNG、JPEG、GIF 或 WebP 的文件
- 默认每轮最多输入 4 个文件、单个 50 MB；只接受常见文档、文本、代码和音频格式，不自动处理压缩包或可执行文件
- 同一 Task 同时只运行一条消息；后续消息进入有界持久队列，默认每个 Task 最多 10 条、全局最多 50 条
- 默认全局最多同时运行 2 个 Task；更多用户或其他 Task 的消息进入队列，避免内存随用户数无界增长
- 队列保留原飞书消息关联和附件资源引用；轮到执行时才下载附件，完成后删除临时文件
- 飞书停止按钮通过 Codex Desktop 中断协议执行；未收到 Desktop 确认时会明确提示用户检查桌面版
- 飞书授权只支持“允许一次”或“拒绝”，不会授予永久权限，也不会绕过 Desktop 的权限模型
- 每轮最多发送 8 张结果图片；本地图片只接受 Codex 明确返回且实际存在的常见图片格式
- 单张待补发结果图片默认最多 20 MB，补发缓存总量默认最多 100 MB；超限会淘汰最旧缓存
- 每轮最多发送 4 个结果文件、单个 50 MB；只处理 Codex 最终回复明确链接的受支持本机文件
- 待补发结果文件复制到私有缓存，总量默认最多 200 MB；普通网页链接不会自动上传
- 桥接不绕过 Codex 的权限、沙箱或用户确认
- 公开仓库不包含任何 App Secret、Token、用户 ID、Chat ID 或本机 Task 数据

## 与 Codex Remote 的当前差异

`1.6.1 (build 24)` 已覆盖已有 Task 续聊、输入/结果附件、最近对话摘要、Task 收藏/最近使用、实时进度、同 Task 排队/跨 Task 并发、停止、一次性授权、新建 Task、搜索/分页/刷新、归档及恢复。Desktop 的“置顶”目前没有可供第三方稳定调用的 Codex app-server 方法，因此桥接不直接修改 Codex SQLite 或 Electron 私有状态；置顶仍在 Desktop 中操作。飞书真实点击、结果文件、文件/音频输入、授权回调和 workflow 决策往返仍需要在目标租户中做端到端验收，本地自测不能替代该证据。

## License

[MIT](LICENSE)
