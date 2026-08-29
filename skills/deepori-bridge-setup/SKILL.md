---
name: deepori-bridge-setup
description: Set up DeepOri Bridge for the first time on macOS by coordinating the local App, Codex Desktop, and a Feishu custom Bot. Use for guided first-connection configuration, not routine bridge operation.
---

# DeepOri Bridge Setup

Configure the installed DeepOri Bridge App on this Mac. The outcome is a running local bridge with one explicitly authorized Feishu user, exact Codex project access, three live event consumers, and a successful text round trip.

## Boundaries

- DeepOri Bridge runs on this Mac. Do not describe Codex as running inside Feishu.
- Do not install a Feishu or Lark plugin for Codex. The App bundles the required `lark-cli` runtime.
- Never ask the user to paste an App Secret into the Codex Task, terminal arguments, files, or logs. Ask them to type it only into the DeepOri Bridge secure field; the App passes it through stdin and stores it in macOS Keychain.
- Never grant `*` project access. The user must explicitly choose exact project names from the Codex Desktop sidebar.
- Do not publish a Feishu app version, approve permissions, add users, or change project access without the user's explicit confirmation at that step.
- Do not copy or commit local bridge config, credentials, user IDs, Chat IDs, Task IDs, state, or logs.

## Workflow

1. Inspect the Mac and report what is already ready:
   - DeepOri Bridge App version and macOS compatibility
   - Codex Desktop installation and at least one local Task
   - current bridge configuration and service status without printing identifiers or secrets
2. Open the DeepOri Bridge first-connection assistant and use its existing Profile when valid. Keep credentials in the App UI.
3. Help configure the Feishu custom app:
   - use browser automation when available and the user is already signed in
   - otherwise give one concrete click instruction at a time
   - enable Bot capability, long-connection delivery, required message/resource permissions, `im.message.receive_v1`, `application.bot.menu_v6`, and `card.action.trigger`
   - configure the menu Event Keys shown by the App, then publish a new Feishu app version
4. Pause only for a real human gate and state exactly what the user must do now:
   - sign in, CAPTCHA, or two-factor authentication
   - enter App ID and App Secret in the App secure fields
   - approve permissions or publish an external Feishu app version
   - select the exact Codex projects a user may access
5. Add the first Feishu user through the App's one-time code flow. Ask the user to send only the displayed code in a P2P Bot chat. Then open user/project settings and save exact project access.
6. Verify separately:
   - App and background service running
   - Bot identity and Feishu network ready
   - three event consumers live
   - authorized-user count and exact project filtering
   - one user-performed text round trip from Feishu to the selected Codex Task and back

If a required browser-control capability is unavailable, continue with precise manual guidance instead of installing unrelated plugins or broadening permissions.
