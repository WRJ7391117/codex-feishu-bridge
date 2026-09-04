# Setup

## Prerequisites

- macOS 13+
- Codex Desktop with at least one local Task
- a Feishu custom app with Bot capability

The App bundles its patched runtime CLI. Add and verify the Feishu Bot from the App home page before opening the first-connection assistant. The home-page form passes App Secret through stdin to `lark-cli`, which stores it in macOS Keychain; never pass an App Secret in process arguments. A system `lark-cli` is optional and only needed for terminal maintenance.

After the Bot connection passes, the first-connection assistant offers two paths. “让 Codex 帮我配置” installs the App-bundled `deepori-bridge-setup` Skill locally and provides a prompt to paste into the user's own Codex Task. It does not require a Feishu or Lark plugin. “我自己手动配置” uses a two-step flow for Bot console settings and explicit user/project authorization. App Secret remains in the home-page secure field and must never be pasted into a Codex Task.

The installed bridge prefers its bundled `lark-cli 1.0.92-codex-feishu.3`. It registers `card.action.trigger` with the SDK's card-action handler and returns a visible processing toast. The system CLI and bundled CLI share the same Profile and Keychain credentials.

Source package `1.11.13 (build 93)` supersedes all earlier builds. Do not downgrade it or overwrite it with a different build carrying the same version.

## Feishu console

Enable long-connection delivery and subscribe to `im.message.receive_v1` and `application.bot.menu_v6`. Enable callback configuration for `card.action.trigger`. Configure a “Task 管理” main menu with four push-event submenus: “当前 Task” with Event Key `current_task`, “切换 Task” with `select_task`, “新建 Task” with `new_task`, and “归档当前 Task” with `archive_task`. Configure a “桌面task” main menu with three push-event submenus in this order: “订阅桌面 Task” with `task_subscriptions`, “接续当前 Task” with `sync_desktop`, and “接续其他 Task” with `sync_desktop_switch`. Configure a “模型设置” main menu with three push-event submenus: “修改当前 Task 模型” with `task_settings`, “压缩当前 Task 上下文” with `compact_task_context`, and “Codex 额度用量” with `codex_usage`. Configure one “提示灯” main menu with “我的提示灯” using `promlight` and “灯光状态说明” using `promlight_legend`. Keep the same twelve values in the Mac App configuration, publish a new Feishu app version, and allow about five minutes for the menu to appear. Bot menus are available only in P2P chats with the Bot.

The bot needs message receive/read/send permissions. Incoming image support also requires permission to read the matching message resource; `im:resource` is needed to upload result images. Follow `missing_scopes` from lark-cli rather than guessing broader permissions.

The first-connection assistant can discover the first permitted user's open_id from one bounded two-minute message listener. It accepts only a user-sent P2P message whose text exactly matches the six-digit one-time code shown by the App, and it does not grant any project. For terminal fallback, consume one bounded message event, then ask the user to send the Bot a test message:

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

Use the resulting `sender_id` in the App's configuration window. The App reads current project names from the Codex Desktop sidebar for explicit multi-selection. Add each approved user separately; `*` remains an explicit manual choice and is never populated automatically. Existing single-user configs are read as that user with `*`; saving from v1.2.0 migrates them to `allowed_users` while retaining the legacy sender field for rollback.

When self-service access requests are enabled, an unknown user may message the Bot in P2P. This records a pending request only. The Mac owner must open the App, choose “配置授权”, and assign exact projects before saving; never populate `*` automatically.

## Install

Run `scripts/install-latest.sh` from this skill after explicit authorization, or download the latest release manually. The installer verifies GitHub's SHA-256 asset digest, bundle identity and version, code signature, arm64/x86_64 architectures, and downgrade direction before replacement. It places the App in `/Applications` when writable, otherwise in `~/Applications`. The App installs runtime files under `~/Library/Application Support/Codex Feishu Bridge/` and preserves an existing config and Task state.

On an existing legacy installation, first launch migrates the old Profile/sender/chat settings and replaces `com.openai.codex.feishu-bridge` with `com.deepori.codex-feishu-bridge`. The old plist is retained as a `.migrated-backup` file.

The installer also replaces the legacy `~/.codex/hooks/feishu_bridge_control.sh` wrapper with the current `com.deepori.codex-feishu-bridge` control script. Before copying any runtime file, it validates every required package resource, the bundled CLI, and all existing config/state/log/runtime destinations with lstat/open checks. Unsafe symlinks, non-regular files, or foreign ownership stop installation. It restricts private directories and files to `0700`/`0600`, then stages runtime files as a complete set and rolls them back on replacement failure. It does not start a bridge that was previously stopped.

The App's “移除后台服务…” action refuses pending work, removes the LaunchAgent and runtime components, and preserves Profile, config, Task state, and logs for recovery. Full purge is separate: run the App-bundled `uninstall.sh --purge` before moving the App to Trash, then type `PURGE` on stdin. The purge validates fixed bridge directories and files before removing them; it never disables Gatekeeper or removes quarantine metadata.

The App uses Sparkle 2 to check a signed GitHub appcast on launch or when a new Feishu event updates the local Bridge state. Event-triggered checks are rate-limited by the previous check time; no periodic update timer is scheduled. Manual “检查更新” remains available. Automatic download and installation after discovery are enabled by default and can be disabled in the control center. Sparkle verifies the release archive with the App's embedded Ed25519 public key. After downloading, the App watches local Bridge state changes and holds installation until there are no active runs, queued inputs, pending replies, or pending Task creations. Runtime synchronization then requests a nonce-bound quiesce: the Bridge stops event intake, drains every in-flight event lane and durable queue, and acknowledges before the installer may stop it. Runtime synchronization is not accepted until all three event consumers report ready; a failed health handshake restores the previous runtime. The legacy `1.11.12` runtime cannot acknowledge this protocol, so its one-time transition keeps the new App but defers runtime replacement until the owner stops Bridge once or it is otherwise not running. A fully quit App does not check in the background; the LaunchAgent remains responsible only for the Bridge runtime.

Source builds are ad-hoc by default. A release operator with a Developer ID certificate and a `notarytool` Keychain profile can set `CODE_SIGN_IDENTITY` and `NOTARY_PROFILE`; the build then enables hardened runtime, waits for notarization, staples the ticket, and regenerates the archive plus SHA-256 manifest. The Release workflow fails closed unless those credentials are available, reads the stable Sparkle private key from GitHub Actions Secrets, and publishes `appcast.xml` alongside the ZIP, SHA-256, and transitional `update.json` through a draft Release. It refuses to replace an existing version's signed assets. The recovery key is kept in the release Mac's login Keychain. Never claim notarization when those credentials were not used.
