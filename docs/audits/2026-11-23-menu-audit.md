# Bot menu audit — 2026-11-23

Base: `1bf54e28edd4278be252e4a99e5f663bec0a8b5f` (`main`).

## Scope and evidence

Audited all twelve configured menu leaves against the current source and installed runtime. Before the fixes their Python sources matched. No production Task, user selection, permission, subscription, or lamp configuration was changed.

| Group | Event keys checked |
| --- | --- |
| Task 管理 | `current_task`, `select_task`, `new_task`, `archive_task` |
| 桌面task | `task_subscriptions`, `sync_desktop`, `sync_desktop_switch` |
| 模型设置 | `task_settings`, `compact_task_context`, `codex_usage` |
| 提示灯 | `promlight`, `promlight_legend` |

- Both authorized users: all 24 isolated handler invocations generated one Card 2.0 response each, with the correct recipient. Local generation took 8–77 ms. Existing authorized Task catalogs were read; state writes were redirected to a private temporary directory. Sending, external subprocesses, hardware reconciliation, and background threads were intercepted. Unauthorized menu invocations emitted no cards.
- Production diagnosis: LaunchAgent running; message, card, and menu consumers ready, with zero dropped events. Bot identity and Feishu connectivity checks passed. No pending delivery backlog was found at initial inspection.
- The two reported `select_task` menu events on November 22 reached the bridge; sending took 1.1–1.4 seconds. Bot message readback confirmed two nondeleted interactive messages created at the matching minute and containing the switch-Task title. This confirms server-side message availability, not mobile rendering or a completed selector interaction.
- Historical database-open errors existed earlier that evening. Subsequent catalog reads and sends succeeded. Those older errors do not establish the cause of the reported clicks.

## Confirmed defects and changes

1. A failed fresh current-Task status-card send was discarded instead of queued. It now uses durable menu delivery with the original idempotency key. After recovery, the delivered message becomes the user's reusable status card. The menu log distinguishes queued delivery from immediate delivery.
2. Retried menu cards lost their explicit callback context. In particular, “接续其他 Task” became an ordinary Task selector after a failed send/restart. The queue now persists and restores its context override.
3. Card-send failures logged only process exit status. They now also log the existing privacy-safe API error categories/code and failure classification; no raw response or credentials are added.

## Validation and remaining boundary

- Baseline: 450 Python tests passed.
- Added two deterministic regressions: current-Task delivery recovery/reuse, and Desktop handoff selector context across restart. Both failed before the fix and passed afterward.
- Final full suite: 452 tests passed. `git diff --check` passed. All 24 isolated menu invocations passed again.
- Existing tests cover project refresh, stale selections, callbacks without action names, cancel flows, Task creation/archive confirmations, model settings, subscriptions, lamp ownership, and unauthorized operations. Their external effects are mocked; they are not new real-device acceptance.
- Native Feishu window inspection failed in the UI automation tool. The reported mobile “no response” remains unconfirmed at the client layer. The exact clicked control (bottom menu, card button, or dropdown) still matters.
- This audit did not modify Feishu's published menu configuration, send test cards to users, run real archive/compaction/model mutations, install these source changes, or publish a Release. The candidate fixes are source/test changes only.
