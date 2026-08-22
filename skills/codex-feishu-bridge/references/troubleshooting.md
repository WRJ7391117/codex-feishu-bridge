# Troubleshooting

Check the layers in order:

1. `lark-cli --version` and `lark-cli --profile <profile> doctor`
2. App config: at least one authorized user; every open_id starts with `ou_`, is unique, and has at least one project rule
3. Codex paths: task state DB and desktop catalog DB exist
4. `launchctl print gui/$(id -u)/com.deepori.codex-feishu-bridge`
5. `~/.codex/log/feishu-bridge-launchd.log` contains ready markers for all three event keys
6. Feishu console has the receive event, bot menu event, and card callback enabled
7. Perform one real message round trip

Common interpretations:

- Listener starts but card selection does nothing: callback configuration is usually not enabled in the Feishu console.
- Bot menu click does nothing: the menu action must be “push event” and its Event Key must match `task_menu_event_key`.
- Every message asks to select again: inspect `state.json` ownership, confirm selection is keyed by that user's open_id, and verify their project rule still includes the selected Task.
- A user sees no Tasks: their project names must exactly match the Codex Desktop sidebar; use `*` only when the Mac owner intends to grant all projects.
- “Task is running”: Codex rejected a concurrent turn. The bridge intentionally does not queue or duplicate it.
- “备用 Codex CLI 版本低于该 task”: Codex Desktop IPC was unavailable and a PATH CLI would be too old for the Task record. Open Codex Desktop and retry; do not delete or reselect the Task.
- “备用 Codex CLI 的兼容版本无法确认”: open Codex Desktop and retry. The bridge blocks an unverifiable CLI fallback to avoid corrupting or misreading the Task record.
- `failed to read thread`, `thread-store internal error`, or `does not start with session metadata`: verify that the installed App is current and that it selects the Desktop-bundled CLI before PATH. The Task rollout may still be valid; do not rebuild the Task solely from this error.
- Start succeeds but no events arrive: a loaded LaunchAgent only proves the process exists; inspect ready markers and console subscriptions.
- Reply fails after Codex completed: inspect lark-cli error envelopes and their `error.type`, `error.subtype`, `missing_scopes`, and `console_url`.
- Codex result text arrives but result images do not: confirm the Bot has `im:resource`, then inspect the bridge log for `image reply failed`. Markdown alone cannot upload a Mac-local path; the bridge must send a separate image reply.
- An incoming Feishu image reports that it cannot be read: confirm the Bot can read that message resource, then inspect the bridge log for `image download failed`. The resource key must belong to the same `message_id`; the bridge intentionally refuses guessed, cross-message, oversized, unsupported, or path-escaping resources.
