# Troubleshooting

Check the layers in order:

1. `lark-cli --version` and `lark-cli --profile <profile> doctor`
2. App config: at least one authorized user; every open_id starts with `ou_`, is unique, and has at least one project rule
3. Codex paths: task state DB and desktop catalog DB exist
4. `launchctl print gui/$(id -u)/com.deepori.codex-feishu-bridge`
5. `~/.codex/log/feishu-bridge.log` contains ready markers for all three event keys; `feishu-bridge-launchd.log` only captures uncaught process output
6. Feishu console has the receive event, bot menu event, and card callback enabled
7. Perform one real message round trip
8. If workflow mode is enabled, run `workflow-config --status`, `workflow-notify --health`, and `workflow-notify --status`; verify both socket files and workflow state are mode 0600

Common interpretations:

- Listener starts but card selection does nothing: callback configuration is usually not enabled in the Feishu console.
- Project/Task selection succeeds but Feishu shows `108002`: verify the installed runtime is using the bundled `lark-cli 1.0.89-codex-feishu.2`. Official v1.0.89 routes the card action through the generic custom-event handler and does not return the card-action response Feishu expects within three seconds.
- Bot menu click does nothing: the menu action must be “push event” and its Event Key must match the corresponding configured key; the usage menu uses `usage_menu_event_key=codex_usage`.
- Every message asks to select again: inspect `state.json` ownership, confirm selection is keyed by that user's open_id, and verify their project rule still includes the selected Task.
- A user sees no Tasks: their project names must exactly match the Codex Desktop sidebar; use `*` only when the Mac owner intends to grant all projects.
- A message stays queued: confirm the active Task eventually completes, inspect `pending input loop failed`, and verify that the Task still exists and remains within that user's project access. If Codex Desktop reports the Task is still busy after a bridge restart, the bridge waits 15 seconds and retries rather than duplicating the turn.
- Progress card never changes: verify the Bot can update its own message and inspect `card patch failed` plus `card patch queued` in the bridge log. Exhausted patches are persisted, coalesced by message ID, and retried in the background after connectivity returns.
- Stop says “未确认”: the local cancellation was recorded but Codex Desktop did not confirm the interrupt; inspect the Task in Desktop before submitting again.
- “已在另一个应用中打开” while a Feishu turn is running: this is the expected writer lock. The Task should still render underneath as a read-only view after the complete-history snapshot arrives. If it remains blank, confirm the installed App is current and inspect Desktop IPC errors before restarting either side.
- An approval card does not appear: inspect Desktop state broadcasts and the bridge log; handle the request in Desktop rather than assuming it was denied.
- “备用 Codex CLI 版本低于该 task”: Codex Desktop IPC was unavailable and a PATH CLI would be too old for the Task record. Open Codex Desktop and retry; do not delete or reselect the Task.
- “备用 Codex CLI 的兼容版本无法确认”: open Codex Desktop and retry. The bridge blocks an unverifiable CLI fallback to avoid corrupting or misreading the Task record.
- “等待你选择执行方式”: the message has not been submitted. Retry Desktop for live Desktop visibility, or explicitly confirm the CLI fallback knowing Desktop will show the Task as opened elsewhere until that run completes.
- `failed to read thread`, `thread-store internal error`, or `does not start with session metadata`: verify that the installed App is current and that it selects the Desktop-bundled CLI before PATH. The Task rollout may still be valid; do not rebuild the Task solely from this error.
- Start succeeds but no events arrive: a loaded LaunchAgent only proves the process exists; inspect ready markers and console subscriptions.
- Reply fails after Codex completed: inspect lark-cli error envelopes and their `error.type`, `error.subtype`, `missing_scopes`, and `console_url`.
- A final text reply that exhausts immediate retries is stored in `state.json` and retried with the original idempotency key. Failed queue cards and the latest failed patch for each progress card are also persisted; successful recovery removes them without rerunning Codex. An old queue card is discarded if its input has already started. Local image replies are not persisted.
- Codex result text arrives but result images do not: confirm the Bot has `im:resource`, then inspect the bridge log for `image reply failed`. Markdown alone cannot upload a Mac-local path; the bridge must send a separate image reply.
- An incoming Feishu image reports that it cannot be read: confirm the Bot can read that message resource, then inspect the bridge log for `image download failed`. The resource key must belong to the same `message_id`; the bridge intentionally refuses guessed, cross-message, oversized, unsupported, or path-escaping resources.
- An incoming file/audio reports that it cannot be read: confirm message-resource read permission, supported suffix, the 50 MB limit, and that the resource key belongs to the same message. Archives, executables, empty files, and path-escaping downloads are intentionally rejected.
- `workflow-notify --dry-run` returns exit 2: the payload is missing a required field, includes an extra field such as recipient/Chat, does not use `workflow_id=ori-one-mind`, points outside the private automation workbench, uses an unsupported status, or has an invalid five-field action list. Do not weaken the schema.
- `workflow-config --status` reports `invalid`: verify the support directory is a current-user `0700` directory and `config.json` is a current-user, non-symlink `0600` regular file. Enabling also requires the legacy sender to remain in `allowed_users` and a canonical dedicated Task UUID from stdin. Do not print or copy the file contents into diagnostics.
- `workflow-notify --health` reports `bridge_unavailable` and workflow sockets/state do not exist: the installed bridge predates workflow support or has not been configured/restarted. Verify the installed App version, install the current package, enable it with `workflow-config`, and then start/restart through the App before testing; do not infer workflow readiness from the three legacy consumers alone.
- `TEST-ROUNDTRIP` reaches the dedicated Task but tries to resolve a Neon attention or advance a research task: stop the test and verify the installed `workflow_recovery_prompt` has the isolated test branch. A valid test only records one receipt and one completed-card patch.
- Workflow status shows a pending notification: check network/lark-cli health, then use `--retry-outbox` only when a real retry is intended. The original event ID preserves idempotency.
- Workflow status shows `delivery_unknown`: Desktop may have accepted the turn without returning confirmation. The bridge will reconcile it only if the same request/action/resolution signature appears in the configured dedicated Task. If it remains unknown, inspect that Task before any manual action; never replay it blindly.
- Workflow ingress returns `workflow_state_unavailable`: do not delete or replace the outbox. Check that `workflow-state.json` is a non-symlink regular file owned by the current user with mode `0600`, then inspect JSON/schema integrity from a backup. The bridge intentionally refuses to recreate an empty state over an unreadable existing file.
- A workflow decision card does not resume: confirm the configured recipient is still allowlisted, the Chat matches, and the fixed Task still exists. The decision remains durable if the Task is busy or Desktop is offline.
