# Troubleshooting

Check the layers in order:

1. `lark-cli --version` and `lark-cli --profile <profile> doctor`
2. App config: at least one authorized user; every open_id starts with `ou_`, is unique, and has at least one project rule
3. Codex paths: task state DB and desktop catalog DB exist
4. `launchctl print gui/$(id -u)/com.deepori.codex-feishu-bridge`
5. `~/.codex/log/feishu-bridge.log` contains ready markers for all three event keys; `feishu-bridge-launchd.log` only captures uncaught process output
6. Feishu console has the receive event, bot menu event, and card callback enabled
7. Perform one real message round trip

Common interpretations:

- Listener starts but card selection does nothing: callback configuration is usually not enabled in the Feishu console.
- Project/Task selection succeeds but Feishu shows `108002`: verify the installed runtime is using the bundled `lark-cli 1.0.89-codex-feishu.3`. Official v1.0.89 routes the card action through the generic custom-event handler and does not return the card-action response Feishu expects within three seconds.
- Project selection visibly takes about one second: inspect the privacy-safe `latency feishu_api` and per-action event records. Local filtering should remain in the millisecond range; roughly 0.9-1.4 seconds is normally Feishu's card-update round trip plus client rendering. If a user taps an old Task list before the new Project renders, the bridge must keep the current Task unchanged and refresh that card to the latest Project instead of reporting a generic failure or switching back.
- Bot menu click does nothing: each clickable submenu must use “push event” and its Event Key must match the corresponding configured key. “Task 管理” is only the parent grouping; its children use `current_task`, `select_task`, `new_task`, and `archive_task`. “管理桌面 Task” groups `task_subscriptions`, `sync_desktop`, and `sync_desktop_switch`, while the top-level “Codex 额度用量” menu uses `codex_usage`.
- Every message asks to select again: inspect `state.json` ownership, confirm selection is keyed by that user's open_id, and verify their project rule still includes the selected Task.
- A user sees no Tasks: their project names must exactly match the Codex Desktop sidebar; use `*` only when the Mac owner intends to grant all projects.
- “管理桌面 Task”→“接续当前 Task” shows an unexpected Task: the card must show that Feishu user's bridge-selected current Task. Cancel or choose “切换到其他 Task”; never expect the bridge to infer the intended Task from Desktop recency. If the current Task changed after the card opened, confirmation is rejected and the card refreshes to the new current Task.
- “管理桌面 Task”→“接续当前 Task” remains running after Desktop completed: verify that the Task rollout still exists and is appendable, inspect `desktop sync loop failed`, and do not delete `state.json`; the subscription cursor is intentionally persisted across bridge restarts.
- A message stays queued: confirm the active Task eventually completes, inspect `pending input loop failed`, and verify that the Task still exists and remains within that user's project access. If Codex Desktop reports the Task is still busy after a bridge restart, the bridge waits 15 seconds and retries rather than duplicating the turn.
- Progress card never changes: verify the Bot can update its own message and inspect `card patch failed` plus `card patch queued` in the bridge log. Exhausted patches are persisted, coalesced by message ID, and retried in the background after connectivity returns.
- Stop says “未确认”: the local cancellation was recorded but Codex Desktop did not confirm the interrupt; inspect the Task in Desktop before submitting again.
- “已在另一个应用中打开” while a Feishu turn is running: this is the expected writer lock. The Task should still render underneath as a read-only view after the complete-history snapshot arrives. If it remains blank, confirm the installed App is current and inspect Desktop IPC errors before restarting either side.
- An approval card does not appear: inspect Desktop state broadcasts and the bridge log; handle the request in Desktop rather than assuming it was denied.
- “备用 Codex CLI 版本低于该 task”: Codex Desktop IPC was unavailable and a PATH CLI would be too old for the Task record. Open Codex Desktop and retry; do not delete or reselect the Task.
- “备用 Codex CLI 的兼容版本无法确认”: open Codex Desktop and retry. The bridge blocks an unverifiable CLI fallback to avoid corrupting or misreading the Task record.
- “等待你选择执行方式”: the message has not been submitted. Retry Desktop for live Desktop visibility, or explicitly confirm the CLI fallback knowing Desktop will show the Task as opened elsewhere until that run completes.
- A CLI-fallback choice remains valid for at most 24 hours. An older card, or an older choice superseded by a newly accepted message for the same user and Task, is intentionally rejected instead of replaying stale work.
- A definite first `no-client-found` automatically opens the exact Task through Codex Desktop's supported deep link, restores the previous foreground app, and retries after a short bounded wait. If activation fails or Desktop still does not claim the Task, the manual Desktop/CLI/cancel card remains available. Pre-submit connection failures and read-only Task-database `OperationalError` failures are also retried briefly. The bridge never retries after a turn request may already have reached Desktop; inspect the Task instead of replaying an uncertain submission.
- `failed to read thread`, `thread-store internal error`, or `does not start with session metadata`: verify that the installed App is current and that it selects the Desktop-bundled CLI before PATH. The Task rollout may still be valid; do not rebuild the Task solely from this error.
- Start succeeds but no events arrive: a loaded LaunchAgent only proves the process exists; inspect ready markers and console subscriptions.
- Diagnostics intentionally retain the bundled `lark-cli 1.0.89-codex-feishu.3`. They suppress only the false same-base `1.0.89` update warning that would remove the bridge patch, while preserving a warning for any genuinely newer upstream base version.
- Reply fails after Codex completed: inspect lark-cli error envelopes and their `error.type`, `error.subtype`, `missing_scopes`, and `console_url`.
- A final text reply that exhausts immediate retries is stored in `state.json` and retried with the original idempotency key. Failed queue cards and the latest failed patch for each progress card are also persisted; successful recovery removes them without rerunning Codex. An old queue card is discarded if its input has already started. Local image replies are not persisted.
- Codex result text arrives but result images do not: confirm the Bot has `im:resource`, then inspect the bridge log for `image reply failed`. Markdown alone cannot upload a Mac-local path; the bridge must send a separate image reply.
- An incoming Feishu image reports that it cannot be read: confirm the Bot can read that message resource, then inspect the bridge log for `image download failed`. The resource key must belong to the same `message_id`; the bridge intentionally refuses guessed, cross-message, oversized, unsupported, or path-escaping resources.
- An incoming file/audio reports that it cannot be read: confirm message-resource read permission, supported suffix, the 50 MB limit, and that the resource key belongs to the same message. Archives, executables, empty files, and path-escaping downloads are intentionally rejected.
