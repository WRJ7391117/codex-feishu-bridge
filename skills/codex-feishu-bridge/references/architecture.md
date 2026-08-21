# Architecture

The bridge has five independent layers:

1. Feishu app: Bot permissions, events, callbacks, and the custom menu.
2. lark-cli: one bot profile and three long-running event consumers.
3. `bridge.py`: user/chat allowlist, project authorization, task catalog, per-user state, deduplication, and replies.
4. Codex Desktop: local task catalog, task rollout files, and desktop IPC.
5. macOS runtime: menu bar App and user LaunchAgent.

Event flow:

```text
Feishu -> lark-cli event consume -> bridge.py -> Codex Desktop IPC
Feishu <- lark-cli message reply <- bridge.py <- Codex task result
```

The bridge consumes:

- `im.message.receive_v1` for text
- `card.action.trigger` for the Task selector
- `application.bot.menu_v6` for the “选择 Task” menu item

Selection is stored by permitted Feishu user, not by individual incoming message. It therefore persists across turns until the user selects a different Task. `state.json` also stores recent message IDs and bridge turn IDs for deduplication.

Each allowlisted user has `allowed_projects`. `*` grants all projects; otherwise names match Codex Desktop sidebar project names exactly. Task access is checked when building the list and again immediately before selection or submission. Removing project access therefore invalidates a previously selected Task. Text, card, and Bot-menu events use their own `sender_id` or `operator_id`, so users do not share selection state.

The primary submission path uses Codex Desktop IPC so the new turn appears in the same desktop Task. If desktop IPC is unavailable, the bridge can fall back to `codex exec resume` when the bundled Codex CLI is discoverable.
