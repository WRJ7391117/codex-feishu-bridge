# DeepOri Bridge for macOS

连接 Codex 与飞书，把飞书 Bot 变成 Codex Desktop 的移动入口：在飞书里按项目选择桌面版左侧栏中的 Task，发送文字、图片、文件或音频，并接收可更新的运行状态和最终结果。

DeepOri Bridge 是独立开源工具，并非 OpenAI、飞书或 Lark 官方产品。

版本说明：当前源码和本机构建为 `1.11.2 (build 81)`，支持 `macOS 13` 或更高版本。不得用旧版本或同版本不同构建覆盖。

面向其他 macOS 用户的 BYOA 产品边界和 2.0 验收标准见 [`docs/PRODUCTIZATION.md`](docs/PRODUCTIZATION.md)，当前已验证与待实机验证范围见 [`docs/TEST_MATRIX.md`](docs/TEST_MATRIX.md)。

## 能力

- 飞书机器人一级菜单为“Task 管理”“桌面task”“模型设置”和“提示灯”；“提示灯”下只提供“我的提示灯”和“灯光状态说明”
- “修改当前 Task 模型”可读取并切换当前 Task 的模型、分析强度与速度；运行中修改会保存给下一条尚未开始的消息，压缩上下文仍要求 Task 空闲并二次确认
- “桌面task”→“接续当前 Task”只读取该飞书用户在桥接中选择的当前 Task，并先显示项目、Task 标题和桌面状态供确认；没有有效当前 Task 时只打开选择卡，不自动猜测其他 Task
- 每位授权用户可独立订阅最多 20 个有权访问的 Task；这些 Task 在 Codex Desktop 完成新运行后自动推送结果，订阅前的历史结果不会补发
- 按 `项目 · Task 标题` 显示所有未归档本地 Task，每页 50 条，可翻页和按标题搜索
- 选中后持续保持当前 Task；并行 Task 返回最终结果时，当前 Task 自动跟随最后送达的结果，便于直接继续追问
- 运行、排队、授权和结果卡片按真实关系显示“当前 Task”“运行 Task”“排队 Task”或“结果所属 Task”，避免切换后旧卡误称当前
- Desktop 尚未加载目标 Task 时，桥接会通过 Desktop 官方 deep link 自动激活一次、恢复原前台 App 后再沿 IPC 提交；只有激活失败才显示“重试 Desktop”“使用备用 CLI”或取消，备用 CLI 仍需用户主动确认
- Desktop 实时同步只订阅当前 turn 的状态变化，不再回放完整历史，避免同一条飞书消息在桌面界面重复显示
- 每位用户有一张持续更新的当前状态卡，集中显示项目、Task、运行/排队状态、最近提问、最近回复和完成时间
- Mac App 显示 Codex 官方接口返回的剩余额度与重置时间；飞书“Codex 额度用量”入口打开的卡片提供实时用量、当日 Task 用量分析和当期 Task 用量分析，不占用当前 Task 状态卡
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
- Desktop 接受运行后立即持久化原消息、进度卡和 turn 游标；桥接重启时卡片改为“恢复中”，新版启动后继续跟踪并用原幂等键补齐完整结果
- 同一 Task 运行中收到的新消息会按顺序排队；不同 Task 可并行，排队卡会区分同 Task 串行、Desktop 忙或全局并发已满
- 可在进度卡中停止当前运行；停止结果会区分“已确认”和“未确认”
- Codex 请求命令、文件修改或临时权限时，可在飞书卡片中“允许一次”或“拒绝”
- 完成后先回复文字结果，再逐张回复 Codex 生成或引用的图片
- Codex 明确链接的 Opus/Ogg Opus 音频会作为飞书原生语音返回；MP3、WAV、M4A 等格式会作为音频附件返回
- Codex 明确链接的 PDF、Office、文本和代码结果文件会自动作为飞书附件返回
- 最终文字、结果图片、结果音频、结果文件和进度卡发送失败都会持久化；网络恢复后补发而不重跑 Codex
- 标准 macOS 主窗口集中显示事件消费者、运行 Task、排队消息、待补发、最近事件、提示灯配置、诊断和日志入口，并可设置 1～8 个并发 Task；提示灯区域直接发现、选择和绑定实体灯，并显示已验证硬件与中继版本
- 菜单栏保留随时开启、关闭和快速打开控制中心的入口
- 用户级 LaunchAgent 常驻，异常退出后自动重启
- App 内置修复卡片回执的专用 `lark-cli`，避免项目或 Task 已选中但飞书仍提示 `108002`
- App 启动时自动检查 GitHub Releases；用户可在控制中心选择是否自动安装更新，开关默认关闭并在重启后保留。自动或手动安装都会校验 SHA-256、App 身份、签名和 Universal 架构，存在运行、排队或待补发工作时不会升级
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
       ├─ 持久跟踪“飞书用户 → 多个 Task → 新桌面结果”订阅
       ├─ 向所有白名单用户展示同一 Mac 账户的 Codex 实时额度
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

首次打开 DeepOri Bridge 后点击“首次连接向导”，可以选择“让 Codex 帮我配置”或“我自己手动配置”。Codex 路线会从 App 内本地安装专用配置 Skill，并生成一段可复制到任意 Codex Task 的指令；不需要安装飞书插件。手动路线继续使用四步向导。

两种路线都要求 App Secret 只在 DeepOri Bridge 的安全输入框中填写。Secret 只通过 stdin 交给内置 `lark-cli`，由它存入 macOS Keychain，不会进入 Codex 对话、进程参数、桥接 `config.json` 或日志。

需要用终端维护时，才使用系统 `lark-cli`：

```bash
lark-cli --profile codex-notify doctor
```

App 运行时优先使用随安装包提供的 `1.0.89-codex-feishu.3`，它在官方 `v1.0.89` 基础上修复 `card.action.trigger` 的 WebSocket 回执类型并同步显示“正在处理…”反馈；Profile 和 Keychain 凭据仍与系统 `lark-cli` 共用。

### 4. 配置飞书开发者后台

在飞书开放平台中完成：

1. 启用机器人能力，并把应用发布到当前租户。
2. 开通消息收发所需权限；至少包括接收单聊消息、读取消息及消息资源、发送/更新消息和上传结果图片（`im:resource`）。文件与音频输入同样依赖读取消息资源。可根据 lark-cli 返回的 `missing_scopes` 精确补充。
3. 在“事件与回调”中启用长连接，并订阅：
   - `im.message.receive_v1`
   - `application.bot.menu_v6`
4. 在“回调配置”中启用卡片回调 `card.action.trigger`。只启动监听但未配置这里时，卡片选择不会产生事件。
5. 在机器人菜单中配置四个主菜单：
   - 主菜单“Task 管理”下添加四个子菜单，动作均选择“推送事件”：
     - “当前 Task” → Event Key `current_task`
     - “切换 Task” → Event Key `select_task`
     - “新建 Task” → Event Key `new_task`
     - “归档当前 Task” → Event Key `archive_task`
   - 主菜单“桌面task”下添加三个子菜单，动作均选择“推送事件”：
     - “订阅桌面 Task” → Event Key `task_subscriptions`
     - “接续当前 Task” → Event Key `sync_desktop`
     - “接续其他 Task” → Event Key `sync_desktop_switch`
   - 主菜单“模型设置”下添加三个子菜单，动作均选择“推送事件”：
     - “修改当前 Task 模型” → Event Key `task_settings`
     - “压缩当前 Task 上下文” → Event Key `compact_task_context`
     - “Codex 额度用量” → Event Key `codex_usage`
   - 主菜单“提示灯”下添加两个子菜单，动作均选择“推送事件”：
     - “我的提示灯” → Event Key `promlight`
     - “灯光状态说明” → Event Key `promlight_legend`
6. 创建并发布新的飞书应用版本；发布成功后菜单可能需要约 5 分钟生效。机器人菜单仅在与 Bot 的单聊中显示。

首次连接向导可启动一次两分钟监听。让机主单聊 Bot 并发送 App 当次显示的六位验证码后，App 才会识别该消息的 `open_id`，但不会自动授予任何项目。终端备用方式如下：

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
5. 确认 App 中的十二个 Event Key 与飞书后台完全一致，然后分别测试模型设置、上下文压缩、额度用量、订阅、接续和提示灯入口。

未授权用户也可以先私聊 Bot 发任意消息。Bot 只会登记访问申请，不会开放任何 Task；机主在 App 的“待审批访问申请”中点击“配置授权”，填写明确项目并保存后才会生效。

关闭主窗口不会关闭后台桥接。再次点击 Dock 中的 App，或点击菜单栏双向箭头 →“打开控制中心”，即可重新打开窗口。

### 6. 卸载或恢复

控制中心的“移除后台服务…”会先确认队列为空，再停止 LaunchAgent 和事件总线、移除运行组件，同时保留飞书 Profile、授权配置、Task 状态和日志。以后重新打开 App 即可恢复安装；要移除 App 本身，再把它移到废纸篓。

只有确认要同时清除本机 Profile、配置、状态和日志时，才运行完整清除；命令会要求在 stdin 中再次输入 `PURGE`：

```bash
"/Applications/Codex 飞书桥接.app/Contents/Resources/bridge/uninstall.sh" --purge
```

## 飞书里的日常操作

- 点击 Bot 菜单“Task 管理”→“当前 Task” → 在聊天底部打开最新状态卡；仅在尚未选择 Task 时进入选择页面
- 点击 Bot 菜单“Task 管理”→“切换 Task” → 卡片只负责先选择项目、再从完整列表中切换到 `项目 · Task 标题`；取消切换时保持原 Task
- 点击 Bot 菜单“桌面task”→“订阅桌面 Task” → 按项目筛选并逐个订阅或取消订阅；订阅不改变当前 Task，Desktop 以后完成的新运行会自动推送，收到结果后当前 Task 跟随该结果以便直接追问
- 点击 Bot 菜单“桌面task”→“接续当前 Task” → 卡片显示本人的桥接当前 Task、所属项目和桌面状态；确认“接续当前 Task”后，已完成结果立即推送，运行中的结果跨桥接重启持续跟踪并在完成后推送
- 点击 Bot 菜单“桌面task”→“接续其他 Task” → 先切换 Task，再通过“接续选定的 Task”确认；没有有效当前 Task 时“接续当前 Task”也会进入同一选择流程，不会自动猜测其他 Task
- 点击 Bot 菜单“模型设置”→“修改当前 Task 模型” → 修改当前 Task 的模型、分析强度和标准/快速速度；设置仅影响下一条尚未开始的消息，运行中的这一轮不会改变
- 点击 Bot 菜单“模型设置”→“压缩当前 Task 上下文” → 查看当前项目和 Task，Task 空闲时二次确认后总结较早内容
- 点击 Bot 菜单“模型设置”→“Codex 额度用量” → 打开独立用量卡；卡片内可继续选择“当日 Task 用量分析”或“当期 Task 用量分析”，查看有权访问项目中的 Task Token 排名、占比、单次模型调用用量、主要消耗原因及是否偏高。“当日”从本地当天 00:00 开始，“当期”与当前 Codex 主额度重置周期一致；Token 用于 Task 间比较和异常诊断，不等同于官方额度或账单的按 Task 扣减
- 点击 Bot 菜单“提示灯”→“我的提示灯” → 查看本人名下提示灯、在线状态、默认灯、当前中继、最后逻辑状态和每灯独立的 Task 白名单；安装包已内置 Universal PromLight Helper，无需另装 PromLight App；设备连接和用户归属在 Mac App 首页完成，飞书卡片再分为“提示灯关联哪些 Task”和“设备设置”两条路径；“解除 Bridge 绑定”会停止提醒并清除关联列表，但不会断开 macOS 蓝牙
- 点击 Bot 菜单“提示灯”→“灯光状态说明” → 查看绿常亮、黄常亮、黄闪、红闪及多 Task 聚合优先级；只有显式关联的 Task 参与，离线与设备 ACK/真实灯效验证严格区分
- 多个 Task 并行时，运行进度不会改变当前 Task；某个 Task 完成、失败或停止并返回最终内容后，当前 Task 自动跟随该结果。下一条消息会直接进入刚返回结果的 Task；随后另一个 Task 再返回时，当前 Task继续跟随最新结果
- 在 Task 卡的“显示范围”中切换“全部 Task / 最近使用 / 我的收藏”；可收藏或取消收藏当前 Task
- 项目 Task 超过 50 条时点击“上一页/下一页”；发送“搜索 关键词”可按标题筛选，卡片中可一键清除搜索
- 点击 Bot 菜单“Task 管理”→“新建 Task” → 在独立卡片中选择项目并确认 → 发送 Task 标题 → Bot 创建并自动选择该 Task；点击“取消新建”可随时退出
- 点击 Bot 菜单“Task 管理”→“归档当前 Task” → 独立卡片显示当前 Task → 可取消或二次确认归档
- 归档成功卡片可“撤销归档”“切换到其他 Task”或“新建 Task”
- 点击 Bot 菜单“Task 管理”→“切换 Task” →“查看已归档 Task”→ 选择并确认恢复；恢复后自动成为当前 Task
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
└── promlight-helper
~/Library/LaunchAgents/com.deepori.codex-feishu-bridge.plist
~/.codex/feishu-bridge/state.json
~/.codex/log/feishu-bridge.log
```

`config.json` 保存 Profile 名、用户白名单、项目权限和菜单设置，不保存 App Secret。`state.json` 按用户分别保存当前 Task、收藏/最近记录、最近对话摘要、桌面结果订阅、访问申请、搜索/分页状态、待执行输入队列，以及尚未送达飞书的最终文字、图片、音频、文件或卡片。本地待补发图片复制到 `~/.codex/feishu-bridge/reply-images/`，音频和文件复制到 `reply-files/`，送达后自动删除。队列和桌面结果订阅跨桥接重启保留；配置、状态与日志都限制为仅当前 macOS 用户可访问。

## 从源码构建

需要 Xcode Command Line Tools、Go 和网络连接。构建脚本会校验官方 `lark-cli v1.0.89` 的固定提交、应用仓库中的最小补丁、运行对应 Go 单测，并生成 Apple Silicon + Intel 通用二进制：

```bash
./scripts/build-app.sh
./scripts/install-local.sh
```

`install-local.sh` 会重新构建并先安全更新 LaunchAgent 实际使用的运行脚本，再复制 App；存在活动飞书运行或待处理队列时会拒绝覆盖。

使用 Developer ID 和 Keychain 中的 notarytool Profile 构建正式公证包：

```bash
CODE_SIGN_IDENTITY="Developer ID Application: Example (TEAMID)" \
NOTARY_PROFILE="codex-feishu-notary" \
./scripts/build-app.sh
```

没有这两个凭证时构建脚本只生成 ad-hoc 包，不会伪装成已公证。每次构建同时生成 `.sha256` 和 `update.json`。

发布时由版本标签触发 `.github/workflows/release.yml`，该工作流是 GitHub Release 的唯一发布入口。发布者只提交并推送版本代码，再推送与 App 版本一致的 `vX.Y.Z` 标签；不要提前手动执行 `gh release create`。若同名 Release 已存在，工作流会用重新构建并校验过的 ZIP、SHA-256 和 `update.json` 覆盖资产并更新发布说明，不会重复创建或直接失败。

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
- 每轮最多发送 4 段结果音频、单段 50 MB；只处理 Codex 最终回复明确链接的受支持本机音频
- Opus（`.opus`）和 Ogg Opus（`.ogg`）使用飞书原生播放器；其他支持格式作为附件发送
- 每轮最多发送 4 个结果文件、单个 50 MB；只处理 Codex 最终回复明确链接的受支持本机文件
- 待补发结果音频和文件复制到私有缓存，总量默认最多 200 MB；普通网页链接不会自动上传
- 桥接不绕过 Codex 的权限、沙箱或用户确认
- 公开仓库不包含任何 App Secret、Token、用户 ID、Chat ID 或本机 Task 数据

## 与 Codex Remote 的当前差异

Desktop 的“置顶”目前没有可供第三方稳定调用的 Codex app-server 方法，因此桥接不直接修改 Codex SQLite 或 Electron 私有状态；置顶仍在 Desktop 中操作。飞书真实点击、结果文件、文件/音频输入和授权回调仍需要在目标租户中做端到端验收，本地自测不能替代该证据。

## License

[MIT](LICENSE)
