# Setup

## Prerequisites

- macOS 13+
- Codex Desktop with at least one local Task
- current `lark-cli`
- a Feishu custom app with Bot capability

Run `lark-cli update` when the user has authorized an update. Configure a dedicated bot profile with `lark-cli config init --name codex-notify`; never pass an App Secret in process arguments.

## Feishu console

Enable long-connection delivery and subscribe to `im.message.receive_v1` and `application.bot.menu_v6`. Enable callback configuration for `card.action.trigger`. Add a custom Bot menu item named “选择 Task”, choose the push-event action, and set its Event Key to `select_task`.

The bot needs message receive/read/send permissions. Follow `missing_scopes` from lark-cli rather than guessing broader permissions.

To discover the permitted user's open_id, consume one bounded message event, then ask the user to send the Bot a test message:

```bash
lark-cli --profile codex-notify event consume im.message.receive_v1 \
  --as bot --max-events 1 --timeout 2m
```

Use the resulting `sender_id` in the App's configuration window.

## Install

Run `scripts/install-latest.sh` from this skill after explicit authorization, or download the latest release manually. The installer places the App in `/Applications` when writable, otherwise in `~/Applications`. The App installs runtime files under `~/Library/Application Support/Codex Feishu Bridge/` and preserves an existing config and Task state.

On an existing legacy installation, first launch migrates the old Profile/sender/chat settings and replaces `com.openai.codex.feishu-bridge` with `com.deepori.codex-feishu-bridge`. The old plist is retained as a `.migrated-backup` file.
