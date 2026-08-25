---
name: codex-feishu-bridge
description: Install, configure, operate, upgrade, or diagnose the Codex Feishu Bridge macOS app that connects Feishu Bot messages to Codex Desktop tasks.
---

# Codex Feishu Bridge

Use this skill for the local macOS bridge published at `WRJ7391117/codex-feishu-bridge`. The bridge lets approved Feishu users select, continue, create, search, and archive permitted Codex Desktop Tasks from Feishu.

## Boundaries

- This is a Mac-resident bridge. Do not describe it as Codex running inside Feishu.
- Treat Codex Task, thread, chat, and conversation as the same object; user-facing wording should use “Task”. A message is one turn inside a Task.
- Never copy or commit `config.json`, lark-cli credentials, `state.json`, logs, user IDs, Chat IDs, or Codex databases.
- Workflow callers may provide only `workflow_id`, `event_id`, `task_id`, `status`, `summary`, `workbench_url`, and `actions`. `workflow_id` is fixed to `ori-one-mind`; action items have exact `id / label / description / recommended / resolution` fields. Recipient, Chat, and dedicated Codex Task identifiers come only from the local 0600 config.
- Workflow-visible summary, labels, and descriptions must contain no credentials, database URLs, private keys, Feishu user IDs, or Chat IDs; the local validator rejects these before persistence or delivery.
- Never grant a new user `*` or add them to the allowlist without the Mac owner's explicit authorization. Project names must match the Codex Desktop sidebar exactly.
- Do not disable Gatekeeper or remove quarantine attributes. An ad-hoc release may require Finder → right-click → Open.
- Start, stop, install, and update are state-changing actions. Confirm the user's intent unless it is already explicit.

## Workflow

1. Inspect before changing anything:
   - macOS version and architecture
   - Codex Desktop installation and `~/.codex` data
   - `lark-cli --version`
   - `lark-cli --profile <name> doctor`
   - current LaunchAgent state
2. Choose the relevant mode:
   - New Mac installation: read [setup.md](references/setup.md), then use `scripts/install-latest.sh` if the user authorizes installation.
   - Existing installation: use the App's main control window first; the menu bar is a shortcut. Use its installed `control.sh` only when automation is necessary.
   - Diagnosis: read [troubleshooting.md](references/troubleshooting.md) and verify each layer independently.
   - Architecture explanation: read [architecture.md](references/architecture.md).
   - Local workflow notification: validate with `workflow-notify --dry-run`, then read [architecture.md](references/architecture.md) before enabling or sending.
3. After a mutation, verify separately:
   - App bundle and signature
   - LaunchAgent loaded state
   - bridge configuration and Codex database discovery
   - authorized-user count and project filtering
   - three event consumers ready
   - an end-to-end Feishu message only when the user is present to perform it
   - for attachment changes, text, image, file, and audio round trips plus the Bot's message-resource permissions
   - for remote-control changes, a real progress-card patch, stop confirmation, and one-time approval callback
   - for workflow changes, dry-run, safe status, durable retry, one-time decision consumption, card completion patch, and recovery into the configured dedicated Task
   - for Task-management changes, verify all five Bot menu Event Keys, including `current_task` and `codex_usage`, then use a disposable authorized-project Task for create/search/archive acceptance; restore archived test data afterward

## Standard commands

```bash
support="$HOME/Library/Application Support/Codex Feishu Bridge"
"$support/control.sh" status
"$support/control.sh" start
"$support/control.sh" stop
"$support/diagnose.sh"
"$support/workflow-notify" --health
"$support/workflow-notify" --status
```

Do not infer service health from a passing Python self-test. A working service requires a loaded LaunchAgent, live event consumers, and an actual message round trip.
