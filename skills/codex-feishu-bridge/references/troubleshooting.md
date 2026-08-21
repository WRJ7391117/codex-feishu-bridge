# Troubleshooting

Check the layers in order:

1. `lark-cli --version` and `lark-cli --profile <profile> doctor`
2. App config: required sender starts with `ou_`
3. Codex paths: task state DB and desktop catalog DB exist
4. `launchctl print gui/$(id -u)/com.deepori.codex-feishu-bridge`
5. `~/.codex/log/feishu-bridge-launchd.log` contains ready markers for all three event keys
6. Feishu console has the receive event, bot menu event, and card callback enabled
7. Perform one real message round trip

Common interpretations:

- Listener starts but card selection does nothing: callback configuration is usually not enabled in the Feishu console.
- Bot menu click does nothing: the menu action must be “push event” and its Event Key must match `task_menu_event_key`.
- Every message asks to select again: inspect `state.json` ownership and ensure selection is keyed by the same allowed sender open_id.
- “Task is running”: Codex rejected a concurrent turn. The bridge intentionally does not queue or duplicate it.
- Start succeeds but no events arrive: a loaded LaunchAgent only proves the process exists; inspect ready markers and console subscriptions.
- Reply fails after Codex completed: inspect lark-cli error envelopes and their `error.type`, `error.subtype`, `missing_scopes`, and `console_url`.
