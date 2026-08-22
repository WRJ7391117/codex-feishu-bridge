# Architecture

The bridge has five independent layers:

1. Feishu app: Bot permissions, events, callbacks, and the custom menu.
2. lark-cli: one bot profile and three long-running event consumers.
3. `bridge.py`: user/chat allowlist, project authorization, task catalog, per-user state, deduplication, and replies.
4. Codex Desktop: local task catalog, task rollout files, and desktop IPC.
5. macOS runtime: menu bar App and user LaunchAgent.

Event flow:

```text
Feishu -> lark-cli event consume -> bridge.py -> optional image download -> Codex Desktop IPC
Feishu <- lark-cli text/image reply <- bridge.py <- Codex task result
```

The bridge consumes:

- `im.message.receive_v1` for text
- `card.action.trigger` for the Task selector
- `application.bot.menu_v6` for the “选择 Task” menu item

Selection is stored by permitted Feishu user, not by individual incoming message. It therefore persists across turns until the user selects a different Task. `state.json` also stores recent message IDs and bridge turn IDs for deduplication.

Each allowlisted user has `allowed_projects`. `*` grants all projects; otherwise names match Codex Desktop sidebar project names exactly. Task access is checked when building the list and again immediately before selection or submission. Removing project access therefore invalidates a previously selected Task. Text, card, and Bot-menu events use their own `sender_id` or `operator_id`, so users do not share selection state.

Text and image messages share the same authorization, selection, and deduplication path. For an image, the bridge extracts only resource keys rendered from that exact Feishu message, downloads each resource as the Bot into a per-turn temporary directory, validates containment, size, and image signature, and deletes the directory after the reply. The default limits are four input images and 20 MB per image.

The primary submission path uses Codex Desktop IPC so the new turn appears in the same desktop Task. Local images are represented as native `localImage` input items. If desktop IPC is unavailable, the bridge prefers the Desktop App's bundled Codex CLI over a PATH installation, passes images with `--image`, and reads the rollout's `session_meta.cli_version` before considering `codex exec resume`. It only uses the fallback when both versions are recognizable and the fallback CLI is not older than the Task record. An incompatible or unverifiable fallback is blocked without clearing the user's selected Task.

For results, the bridge sends text first and images afterward. It recognizes local or remote Markdown image references in the final message and also collects `image_generation_end.saved_path` events written during the current rollout segment. These image events do not contain a turn ID, so the bridge scopes them by the rollout byte offset recorded immediately before starting the only active turn. Local images are uploaded from their parent directory because lark-cli requires a relative `--image` path. The default per-turn limit is eight images.
