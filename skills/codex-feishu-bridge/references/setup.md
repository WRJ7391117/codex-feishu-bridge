# Setup

## Prerequisites

- macOS 13+
- Codex Desktop with at least one local Task
- a Feishu custom app with Bot capability

The App bundles its patched runtime CLI. Use the first-connection assistant to configure a dedicated Profile. It passes App Secret through stdin to `lark-cli`, which stores it in macOS Keychain; never pass an App Secret in process arguments. A system `lark-cli` is optional and only needed for terminal maintenance.

The installed bridge prefers its bundled `lark-cli 1.0.89-codex-feishu.3`. It registers `card.action.trigger` with the SDK's card-action handler, returns a visible processing toast, and durably spools Ori One workflow decisions before returning Feishu's synchronous callback response. The system CLI and bundled CLI share the same Profile and Keychain credentials. General bridge use may explicitly override `lark_cli_path`, but workflow mode fails closed unless it uses the bundled CLI.

Source package `1.9.12 (build 48)` supersedes all earlier builds. Do not downgrade it or overwrite it with a different build carrying the same version.

## Feishu console

Enable long-connection delivery and subscribe to `im.message.receive_v1` and `application.bot.menu_v6`. Enable callback configuration for `card.action.trigger`. Configure a `TASK` main menu with four push-event submenus: “当前 Task” with Event Key `current_task`, “切换 Task” with `select_task`, “新建 Task” with `new_task`, and “归档当前 Task” with `archive_task`. Configure a top-level “接续桌面 Task” menu with two push-event submenus: “接续当前 Task” with `sync_desktop` and “接续其他 Task” with `sync_desktop_switch`. Add top-level “订阅 Task” with `task_subscriptions` and top-level “额度用量” with `codex_usage`. Keep the same eight values in the Mac App configuration, publish a new Feishu app version, and allow about five minutes for the menu to appear. Bot menus are available only in P2P chats with the Bot.

The bot needs message receive/read/send permissions. Incoming image support also requires permission to read the matching message resource; `im:resource` is needed to upload result images. Follow `missing_scopes` from lark-cli rather than guessing broader permissions.

The first-connection assistant can discover the first permitted user's open_id from one bounded two-minute message listener. It does not grant any project. For terminal fallback, consume one bounded message event, then ask the user to send the Bot a test message:

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

Use the resulting `sender_id` in the App's configuration window. The App reads current project names from the Codex Desktop sidebar for explicit multi-selection. Add each approved user separately; `*` remains an explicit manual choice and is never populated automatically. Existing single-user configs are read as that user with `*`; saving from v1.2.0 migrates them to `allowed_users` while retaining the legacy sender field for rollback.

When self-service access requests are enabled, an unknown user may message the Bot in P2P. This records a pending request only. The Mac owner must open the App, choose “配置授权”, and assign exact projects before saving; never populate `*` automatically.

## Install

Run `scripts/install-latest.sh` from this skill after explicit authorization, or download the latest release manually. The installer places the App in `/Applications` when writable, otherwise in `~/Applications`. The App installs runtime files under `~/Library/Application Support/Codex Feishu Bridge/` and preserves an existing config and Task state.

On an existing legacy installation, first launch migrates the old Profile/sender/chat settings and replaces `com.openai.codex.feishu-bridge` with `com.deepori.codex-feishu-bridge`. The old plist is retained as a `.migrated-backup` file.

The installer also replaces the legacy `~/.codex/hooks/feishu_bridge_control.sh` wrapper with the current `com.deepori.codex-feishu-bridge` control script. Before copying any runtime file, it validates every required package resource, the bundled CLI, and all existing config/state/log/runtime destinations with lstat/open checks. Unsafe symlinks, non-regular files, or foreign ownership stop installation. It restricts private directories and files to `0700`/`0600`, then stages runtime files as a complete set and rolls them back on replacement failure. It does not start a bridge that was previously stopped.

The App's “移除后台服务…” action refuses pending work, removes the LaunchAgent and runtime components, and preserves Profile, config, Task state, and logs for recovery. Full purge is separate: run the App-bundled `uninstall.sh --purge` before moving the App to Trash, then type `PURGE` on stdin. The purge validates fixed bridge directories and files before removing them; it never disables Gatekeeper or removes quarantine metadata.

## Optional private extension: Ori One workflow notifications

This extension is not part of the general bridge onboarding or public product promise. Keep workflow notifications disabled unless the Mac is an explicitly configured Ori One private deployment and the dedicated Codex Task exists. Do not use generic `jq` or command arguments to edit local identifiers. The installed `workflow-config --enable` reads only the dedicated Task UUID from stdin, selects the existing legacy sender only when it is already allowlisted, and leaves Chat unguessed. A returned Chat is associated with the durable notification record after the first card is sent.

```bash
support="$HOME/Library/Application Support/Codex Feishu Bridge"
"$support/workflow-config" --status
read -r workflow_task_id
printf '%s\n' "$workflow_task_id" | "$support/workflow-config" --enable
unset workflow_task_id
```

The config tool prints only `configured`, `disabled`, or `invalid`. `workflow-config --disable` writes the disabled state while preserving any existing local binding. The bridge reads config only at process start, so restart it once from the App control window after either enabling or disabling before checking health.

Validate before a real send:

```bash
support="$HOME/Library/Application Support/Codex Feishu Bridge"
"$support/workflow-notify" --health
"$support/workflow-notify" --dry-run < event.json
"$support/workflow-notify" --status
```

Only the final command without `--dry-run` enqueues a new proactive notification. `--retry-outbox` can cause a due queued item to make a real API/IPC attempt, so treat it as a state-changing operation.

With the user present for the required end-to-end check, run `workflow-notify --roundtrip-test`. It creates a unique `TEST-ROUNDTRIP` card. Its reply enters the fixed Codex Task once, patches the completed card once, and only reports a test receipt: it must not call Neon, `resolve-attention`, or the orchestrator; edit files; or lease/advance a `ONE-*` task.

The bridge alone owns the one 24-hour reminder for an unanswered decision. Keep reminder generation disabled in Neon and the deterministic runner.

The App automatically checks the latest GitHub Release when its control window opens. It can install an update only from `/Applications` or `~/Applications`, and only after the three pending-work queues are empty and the GitHub SHA-256, bundle identity/version, code signature, and Universal architectures pass. The helper logs aggregate update results to `~/.codex/log/feishu-bridge-app-update.log` and restores the previous App if replacement or launch fails. Source builds are ad-hoc by default. A release operator with a Developer ID certificate and a `notarytool` Keychain profile can set `CODE_SIGN_IDENTITY` and `NOTARY_PROFILE`; the build then enables hardened runtime, waits for notarization, staples the ticket, and regenerates the archive plus SHA-256 manifest. Never claim notarization when those credentials were not used.
