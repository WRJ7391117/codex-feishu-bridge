---
name: deepori-bridge-setup
description: Set up DeepOri Bridge for the first time on macOS by coordinating the local App, Codex Desktop, and a Feishu custom Bot. Use for guided first-connection configuration, not routine bridge operation.
---

# DeepOri Bridge Setup

Configure the installed DeepOri Bridge App on this Mac. The outcome is a running local bridge with one explicitly authorized Feishu user, exact Codex project access, three live event consumers, and a successful text round trip.

## Boundaries

- DeepOri Bridge runs on this Mac. Do not describe Codex as running inside Feishu.
- Do not install a Feishu or Lark plugin for Codex. The App bundles the required `lark-cli` runtime.
- Never ask the user to paste an App Secret into the Codex Task, terminal arguments, files, or logs. Ask them to type it only into the DeepOri Bridge secure field; the App passes it through stdin and stores it in macOS Keychain.
- Never grant `*` project access. The user must explicitly choose exact project names from the Codex Desktop sidebar.
- Do not publish a Feishu app version, approve permissions, add users, or change project access without the user's explicit confirmation at that step.
- Do not copy or commit local bridge config, credentials, user IDs, Chat IDs, Task IDs, state, or logs.

## Workflow

1. Inspect the Mac and report what is already ready:
   - DeepOri Bridge App version and macOS compatibility
   - Codex Desktop installation and at least one local Task
   - current bridge configuration and service status without printing identifiers or secrets
2. Confirm the DeepOri Bridge home page shows a verified Feishu Bot connection before opening the first-connection assistant. If it does not, pause and ask the user to use “添加 Bot” on the home page. Keep App ID and App Secret in that App-owned form and never move them into Codex.
3. Open the DeepOri Bridge first-connection assistant and use its verified Profile.
4. Help configure the Feishu custom app:
   - use browser automation when available and the user is already signed in
   - otherwise give one concrete click instruction at a time
   - enable Bot capability, long-connection delivery, required message/resource permissions, `im.message.receive_v1`, `application.bot.menu_v6`, and `card.action.trigger`
   - configure the exact menu contract below, then publish a new Feishu app version
5. Pause only for a real human gate and state exactly what the user must do now:
   - sign in, CAPTCHA, or two-factor authentication
   - approve permissions or publish an external Feishu app version
   - select the exact Codex projects a user may access
6. Add the first Feishu user through the App's one-time code flow. Ask the user to send only the displayed code in a P2P Bot chat. Then open user/project settings and save exact project access.
7. Verify separately:
   - App and background service running
   - Bot identity and Feishu network ready
   - three event consumers live
   - authorized-user count and exact project filtering
   - one user-performed text round trip from Feishu to the selected Codex Task and back

If a required browser-control capability is unavailable, continue with precise manual guidance instead of installing unrelated plugins or broadening permissions.

## Exact Bot menu contract

Create exactly these four first-level menus in this order. Every child item must use the Feishu action “推送事件”. Keep every menu name and Event Key exactly as written; do not translate, rename, omit, reorder, or invent items.

1. `Task 管理`
   - `当前 Task` → `current_task`
   - `切换 Task` → `select_task`
   - `新建 Task` → `new_task`
   - `归档当前 Task` → `archive_task`
2. `桌面task`
   - `订阅桌面 Task` → `task_subscriptions`
   - `接续当前 Task` → `sync_desktop`
   - `接续其他 Task` → `sync_desktop_switch`
3. `模型设置`
   - `修改当前 Task 模型` → `task_settings`
   - `压缩当前 Task 上下文` → `compact_task_context`
   - `Codex 额度用量` → `codex_usage`
4. `提示灯`
   - `我的提示灯` → `promlight`
   - `灯光状态说明` → `promlight_legend`

Before publication, compare all twelve Event Keys with DeepOri Bridge → “配置授权” → “机器人菜单 Event Key”. A visually similar menu with a different Event Key is not valid.
