# Codex 飞书桥接

把飞书 Bot 变成 Codex Desktop 的移动入口：在飞书里选择桌面版左侧栏中的 Task，发送文字或图片，接收运行状态以及文字和图片结果。

## 能力

- 飞书机器人菜单一键打开 Task 选择卡片
- 按 `项目 · Task 标题` 显示所有未归档本地 Task，不限 10 条
- 选中后持续保持当前 Task，直到主动切换
- 指定飞书用户白名单，每位用户独立保持自己的当前 Task
- 按 Codex 项目限制每位用户可查看和提交的 Task
- 将飞书文字和图片提交给同一个 Codex Desktop Task
- 支持单张图片消息和富文本中的多张图片，默认每轮最多 4 张、单张 20 MB
- 先回复运行状态，完成后先回复文字结果，再逐张回复 Codex 生成或引用的图片
- 标准 macOS 主窗口集中显示状态、配置、诊断和日志入口
- 菜单栏保留随时开启、关闭和快速打开控制中心的入口
- 用户级 LaunchAgent 常驻，异常退出后自动重启
- 支持 Apple Silicon 和 Intel Mac

## 原理

```text
飞书文字 / 图片 / 卡片 / Bot 菜单
            ↓ WebSocket 长连接
         lark-cli
            ↓ NDJSON 事件
     本地 bridge.py
       ├─ 读取 Codex Desktop 本地 Task 目录
       ├─ 检查“飞书用户 → 允许项目”权限
       ├─ 保存“飞书用户 → 当前 Task”状态
       ├─ 将飞书图片下载到本轮临时目录
       └─ 通过 Codex Desktop IPC 继续同一个 Task
                         ↓
                  Codex 执行与输出
                         ↓
       lark-cli 回复运行状态、文字和图片结果
                         ↓
                       飞书
```

这里的 Task 就是 Codex Desktop 左侧栏中的一条对话。项目是 Task 的归类；每次发送文字或图片只是该 Task 内的一轮，不会新建 Task。

桥接在 Mac 本机运行，因此 Mac 必须开机、联网，Codex Desktop 的本地数据必须可用。飞书 Bot 本身不运行 Codex。

提交时优先使用 Codex Desktop IPC。若 Desktop 暂时未连接，桥接只会在备用 Codex CLI 与该 Task 的本地记录版本兼容时回退；默认优先使用 Desktop App 内置 CLI，而不是 PATH 中可能过旧的全局 CLI。版本无法确认或不兼容时，Bot 会提示在 Mac 打开 Codex Desktop 后重试，当前 Task 选择不会丢失。

## 安装

### 1. 前置条件

- macOS 13 或更新版本
- 已安装并使用 Codex Desktop / ChatGPT Desktop
- 已安装 [lark-cli](https://github.com/larksuite/cli)
- 一个启用了机器人能力的飞书自建应用

更新 lark-cli：

```bash
lark-cli update
```

### 2. 下载 App

从 [Releases](https://github.com/WRJ7391117/codex-feishu-bridge/releases) 下载 `Codex-Feishu-Bridge-macOS-universal.zip`，解压后把 `Codex 飞书桥接.app` 拖入“应用程序”。

当前公开包使用 ad-hoc 签名，尚未经过 Apple 公证。另一台 Mac 首次打开时，请在 Finder 中右键 App →“打开”。不要用脚本移除系统隔离属性。

### 3. 配置 lark-cli Profile

推荐为桥接建立独立 Profile：

```bash
lark-cli config init --name codex-notify
lark-cli --profile codex-notify doctor
```

App Secret 由 lark-cli / macOS Keychain 管理，不写入本仓库或桥接的 `config.json`。

### 4. 配置飞书开发者后台

在飞书开放平台中完成：

1. 启用机器人能力，并把应用发布到当前租户。
2. 开通消息收发所需权限；至少包括接收单聊消息、读取消息资源、发送消息和上传结果图片（`im:resource`）。可根据 lark-cli 返回的 `missing_scopes` 精确补充。
3. 在“事件与回调”中启用长连接，并订阅：
   - `im.message.receive_v1`
   - `application.bot.menu_v6`
4. 在“回调配置”中启用卡片回调 `card.action.trigger`。只启动监听但未配置这里时，卡片选择不会产生事件。
5. 在机器人菜单中添加“选择 Task”：动作选择“推送事件”，Event Key 填 `select_task`。

可用下面的临时监听获得用户的 `open_id`。启动后让该用户给 Bot 发一条消息，输出中的 `sender_id` 即 `ou_...`：

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

### 5. 配置并开启桥接

打开 `Codex 飞书桥接.app`，主控制窗口会自动出现：

1. 点击“配置桥接”；
2. 在“授权用户”中填写备注名、`open_id` 和允许项目；
   - `*` 表示允许访问全部项目；
   - 多个项目用逗号分隔，名称必须与 Codex Desktop 左侧栏完全一致；
   - 点击“添加用户”可继续添加白名单用户；
3. 群 Chat ID 可留空，先使用与 Bot 的单聊；
4. 点击“开启桥接”；
5. 点击飞书 Bot 菜单中的“选择 Task”。

关闭主窗口不会关闭后台桥接。再次点击 Dock 中的 App，或点击菜单栏双向箭头 →“打开控制中心”，即可重新打开窗口。

## 飞书里的日常操作

- 点击 Bot 菜单“选择 Task” → 卡片下拉框显示 `项目 · Task 标题`
- 选中一次后直接发送文字 → 始终继续当前 Task
- 直接发送一张图片 → 图片进入当前 Task；没有附带文字时会使用中性的图片提示
- 富文本消息可同时包含文字和多张图片
- Codex 返回图片 → Bot 先回复文字，再把图片回复在同一条消息下
- 再次点击“选择 Task” → 切换到另一个 Task
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
└── diagnose.sh
~/Library/LaunchAgents/com.deepori.codex-feishu-bridge.plist
~/.codex/feishu-bridge/state.json
~/.codex/log/feishu-bridge.log
```

`config.json` 保存 Profile 名、用户白名单和项目权限，不保存 App Secret。`state.json` 按用户分别保存当前 Task、最近列表、授权过的单聊/群聊状态，以及尚未送达飞书的最终文字结果。待补发结果跨桥接重启保留，发送成功后自动清除。

## 从源码构建

需要 Xcode Command Line Tools：

```bash
./scripts/build-app.sh
./scripts/install-local.sh
```

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
- 提交结果不确定时不会自动重复执行，避免同一任务运行两次
- 输入图片只从当前飞书消息的资源键下载，限制在本轮临时目录，并在回复完成后删除
- 默认每轮最多输入 4 张图片、单张 20 MB，只接受实际内容为 PNG、JPEG、GIF 或 WebP 的文件
- 每轮最多发送 8 张结果图片；本地图片只接受 Codex 明确返回且实际存在的常见图片格式
- 桥接不绕过 Codex 的权限、沙箱或用户确认
- 公开仓库不包含任何 App Secret、Token、用户 ID、Chat ID 或本机 Task 数据

## License

[MIT](LICENSE)
