# Codex 飞书桥接

把飞书 Bot 变成 Codex Desktop 的移动入口：在飞书里选择桌面版左侧栏中的 Task，发送文字，接收“正在运行”和最终结果。

## 能力

- 飞书机器人菜单一键打开 Task 选择卡片
- 按 `项目 · Task 标题` 显示所有未归档本地 Task，不限 10 条
- 选中后持续保持当前 Task，直到主动切换
- 将飞书文字提交给同一个 Codex Desktop Task
- 先回复运行状态，完成后再回复最终结果
- 菜单栏 App 随时开启、关闭、配置和诊断
- 用户级 LaunchAgent 常驻，异常退出后自动重启
- 支持 Apple Silicon 和 Intel Mac

## 原理

```text
飞书消息 / 卡片 / Bot 菜单
            ↓ WebSocket 长连接
         lark-cli
            ↓ NDJSON 事件
     本地 bridge.py
       ├─ 读取 Codex Desktop 本地 Task 目录
       ├─ 保存“飞书用户 → 当前 Task”状态
       └─ 通过 Codex Desktop IPC 继续同一个 Task
                         ↓
                  Codex 执行与输出
                         ↓
       lark-cli 回复运行状态和最终结果
                         ↓
                       飞书
```

这里的 Task 就是 Codex Desktop 左侧栏中的一条对话。项目是 Task 的归类；每次输入文字只是该 Task 内的一轮，不会新建 Task。

桥接在 Mac 本机运行，因此 Mac 必须开机、联网，Codex Desktop 的本地数据必须可用。飞书 Bot 本身不运行 Codex。

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
2. 开通消息收发所需权限；至少包括接收单聊消息、读取消息和发送消息。可根据 lark-cli 返回的 `missing_scopes` 精确补充。
3. 在“事件与回调”中启用长连接，并订阅：
   - `im.message.receive_v1`
   - `application.bot.menu_v6`
4. 在“回调配置”中启用卡片回调 `card.action.trigger`。只启动监听但未配置这里时，卡片选择不会产生事件。
5. 在机器人菜单中添加“选择 Task”：动作选择“推送事件”，Event Key 填 `select_task`。

可用下面的临时监听获得自己的 `open_id`。启动后给 Bot 发一条消息，输出中的 `sender_id` 即 `ou_...`：

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

### 5. 配置并开启桥接

打开 `Codex 飞书桥接.app`，在 macOS 菜单栏点击双向箭头图标：

1. 点击“配置…”；
2. 填入 lark-cli Profile 和允许的用户 `open_id`；
3. 群 Chat ID 可留空，先使用与 Bot 的单聊；
4. 点击“开启桥接”；
5. 点击飞书 Bot 菜单中的“选择 Task”。

## 飞书里的日常操作

- 点击 Bot 菜单“选择 Task” → 卡片下拉框显示 `项目 · Task 标题`
- 选中一次后直接发送文字 → 始终继续当前 Task
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

`config.json` 保存 Profile 名和身份白名单，不保存 App Secret。`state.json` 保存当前 Task、去重记录和授权过的单聊/群聊状态。

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

- 默认只接受配置中的单个飞书 `open_id`
- 群聊必须显式列入允许列表，或先由同一用户通过受信任卡片建立状态
- 消息按飞书 `message_id` 去重，回复使用幂等键
- 提交结果不确定时不会自动重复执行，避免同一任务运行两次
- 桥接不绕过 Codex 的权限、沙箱或用户确认
- 公开仓库不包含任何 App Secret、Token、用户 ID、Chat ID 或本机 Task 数据

## License

[MIT](LICENSE)
