# Setup

## Prerequisites

- macOS 13+
- Codex Desktop with at least one local Task
- a Feishu custom app with Bot capability

The App bundles its patched runtime CLI. Use the first-connection assistant to configure a dedicated Profile. It passes App Secret through stdin to `lark-cli`, which stores it in macOS Keychain; never pass an App Secret in process arguments. A system `lark-cli` is optional and only needed for terminal maintenance.

The installed bridge prefers its bundled `lark-cli 1.0.89-codex-feishu.3`. It registers `card.action.trigger` with the SDK's card-action handler and returns a visible processing toast. The system CLI and bundled CLI share the same Profile and Keychain credentials.

Source package `1.9.23 (build 59)` supersedes all earlier builds. Do not downgrade it or overwrite it with a different build carrying the same version.

## Feishu console

Enable long-connection delivery and subscribe to `im.message.receive_v1` and `application.bot.menu_v6`. Enable callback configuration for `card.action.trigger`. Configure a “Task 管理” main menu with four push-event submenus: “当前 Task” with Event Key `current_task`, “切换 Task” with `select_task`, “新建 Task” with `new_task`, and “归档当前 Task” with `archive_task`. Configure a “管理桌面 Task” main menu with three push-event submenus in this order: “订阅桌面 Task” with `task_subscriptions`, “接续当前 Task” with `sync_desktop`, and “接续其他 Task” with `sync_desktop_switch`. Add top-level “Codex 额度用量” with `codex_usage`. Keep the same eight values in the Mac App configuration, publish a new Feishu app version, and allow about five minutes for the menu to appear. Bot menus are available only in P2P chats with the Bot.

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

The App automatically checks the latest GitHub Release when its control window opens. It can install an update only from `/Applications` or `~/Applications`, and only after the three pending-work queues are empty and the GitHub SHA-256, bundle identity/version, code signature, and Universal architectures pass. The helper logs aggregate update results to `~/.codex/log/feishu-bridge-app-update.log` and restores the previous App if replacement or launch fails. Source builds are ad-hoc by default. A release operator with a Developer ID certificate and a `notarytool` Keychain profile can set `CODE_SIGN_IDENTITY` and `NOTARY_PROFILE`; the build then enables hardened runtime, waits for notarization, staples the ticket, and regenerates the archive plus SHA-256 manifest. Never claim notarization when those credentials were not used.
