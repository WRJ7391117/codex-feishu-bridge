---
name: deepori-bridge-setup
description: Set up DeepOri Bridge for the first time on Windows 11 by coordinating the local App, Codex Desktop, and a Feishu custom Bot.
---

# DeepOri Bridge Setup for Windows

Configure DeepOri Bridge on this Windows 11 computer. The target is a local, read-only bridge with one explicitly authorized Feishu owner and only explicitly selected Codex project access.

## Boundaries

> **Fail-closed incident block:** Codex-assisted setup is disabled until the cross-app identity contract passes independent audit. This skill may explain the manual flow only. It must not initialize an app, start a service, write state, invoke a change confirmation, or publish anything.

- Never discover the target with `Get-AppxPackage | Where Name/PackageFullName -match DeepOri`, `Select-Object -First 1`, fuzzy titles, result order, or any other broad DeepOri match.
- `DeepOri.AgentMesh`, `DeepOriAgentMesh.exe`, and pages containing “初始化本机节点” or “启动本地服务” are the separate DeepOri Agent Mesh product. Stop immediately if any appear. Do not initialize, start, stop, delete, or roll back Mesh data.
- A Bridge candidate is valid only if the same candidate matches all available exact signals: executable `DeepOriBridge.exe`, product metadata `DeepOri Bridge for Windows`, exact package name `E1E677B4-5C45-4603-818D-FA1592843ABC` when packaged, and page AutomationId prefix `DeepOriBridge.`. Zero or multiple candidates fail closed.
- Before every UI action, re-check the window process path, product metadata, exact identity, page AutomationId, and the Bridge session nonce supplied by the App prompt. A stale/different nonce fails closed.
- Existing credentials do not prove App identity. The App must show a verified App ID fingerprint, and the fingerprint must match before any long connection may start.

- DeepOri Bridge and Codex Desktop run on this Windows computer; Codex does not run inside Feishu.
- Do not install a Feishu or Lark plugin for Codex.
- Never ask for an App Secret in a Codex Task, command line, file, state, or log. The user enters it only in the Bridge PasswordBox, and the App stores it in the current Windows user's Credential Locker.
- Never grant wildcard or all-project access. Require an explicit project selection.
- Do not publish a Feishu version, approve permissions, add an owner, or change project access without a real human confirmation at that step.
- Do not copy or commit credentials, user IDs, chat IDs, Task IDs, configuration, state databases, or logs.

## Workflow

1. Read-only inspect Windows 11 and Codex Desktop. Do not enumerate or activate DeepOri application candidates while the incident block is active.
2. Ask the user to open the real Bridge manually and use its first-connection assistant. Keep App ID and App Secret entry inside that App.
3. Help configure the Feishu custom app: Bot capability, long-connection delivery, required permissions, `im.message.receive_v1`, `application.bot.menu_v6`, `card.action.trigger`, and a published version.
4. Pause only for sign-in/2FA, App credential entry in the Bridge, permission approval or publication, and explicit project authorization.
5. Bind a user through the App's random six-digit, two-minute one-time code flow. Do not expose the sender ID. Save at least one explicit existing project directory; wildcard/all-project access is rejected.
6. Verify separately that the Bridge is running, the Feishu connection is listening, owner binding is active, project access is explicit, and one user-performed text round trip succeeds.

Required Feishu long-connection consumers are `im.message.receive_v1`, `application.bot.menu_v6`, and `card.action.trigger`. The 12 menu Event Keys are `current_task`, `select_task`, `new_task`, `archive_task`, `task_settings`, `compact_task_context`, `codex_usage`, `sync_desktop`, `sync_desktop_switch`, `task_subscriptions`, `promlight`, and `promlight_legend`; verify the first-level menu hierarchy shown by the Bridge checklist. Do not treat subscription configuration as a live callback: each consumer needs separate runtime evidence.

If browser control is unavailable, continue with one precise manual instruction at a time. Do not broaden permissions or install unrelated plugins. While the incident block remains, do not perform any mutation even when browser control is available.

Installed and updated by DeepOri Bridge for Windows.
