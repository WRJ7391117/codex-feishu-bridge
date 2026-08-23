# Release Notes

## 1.5.8 (build 20)

- Creates and names a new Codex Task in one app-server session, using the current `thread/start` contract without the removed `projectId` field.
- Keeps the requested project working directory on the first Feishu turn and restores the user-supplied Task title after Codex generates its first automatic title.
- Resolves a newly created empty Task directly from the Codex state database while the Desktop sidebar catalog is still refreshing.
- Adds an explicit “取消，不归档” action that leaves the current Task selected and performs no Codex archive call.
- Lists authorized archived Tasks from the existing Task card and restores them through the official `thread/unarchive` app-server method.
- Adds “撤销归档”“选择其他 Task”“新建 Task” actions to the completed archive card so users do not end in an unselected dead end.
- Adds regression coverage for new-Task naming/directory preservation, active-versus-archived catalog filtering, cancel semantics, restore callbacks, and post-archive actions.
- Supersedes the local-only `1.5.4`—`1.5.7` builds and public `1.5.3 (build 15)`; do not publish these fixes under a reused version/build pair.

## 1.5.3 (build 15)

- Makes the durable WorkflowStore decision the only correctness authority before any auxiliary event deduplication, so a transient state failure or crash cannot consume a Feishu retry without resuming the workflow.
- Patches both the initial decision card and its one-time 24-hour reminder after a choice, with existing durable card-patch retry handling for network failures.
- Spools workflow card callbacks to a private fsynced inbox before the bundled `lark-cli` returns Feishu's ACK, re-syncs the inbox directory on duplicate delivery, then replays and acknowledges them across bridge restarts without creating a second recovery.
- Persists workflow-choice replies and card-patch retries through private atomic state writes with file and directory fsync before reporting them queued.
- Validates existing `config.json` and `workflow-state.json` JSON/schema before the installer creates directories, changes permissions, replaces runtime files, or stops the old service.
- Adds card and text-reply regression tests for state-save failures, side-effect crash windows, same-event restart recovery, dual-card durable patching, and installer fail-closed behavior.
- Supersedes `1.5.2 (build 14)`; do not publish these fixes under a reused version/build pair.

## 1.5.2 (build 14)

- Finalizes the Ori One Mind proactive workflow contract: fixed workflow ID, exact action schema, private workbench URL allowlist, and one-time decision recovery through the dedicated Codex Task.
- Makes workflow outbox state fail closed on corruption, unsafe permissions, symlinks, ownership changes, or schema drift; atomic writes now fsync both the state file and its directory.
- Reconciles uncertain Task recovery only from the matching dedicated-Task user input and never blindly resubmits it.
- Rejects obvious credentials, database URLs, private keys, and Feishu user/Chat identifiers from notification text before it can reach a card or Task prompt.
- Adds the identifier-safe local `workflow-config` tool and the isolated `TEST-ROUNDTRIP` card/reply check; repeated callbacks consume and patch only once.
- Preflights all package resources and existing private files before runtime replacement, with staged runtime rollback on failure.
- Supersedes `1.5.1 (build 13)` and the previously planned `1.4.3`; do not publish these runtime changes under an older or reused version/build pair.
