# Codex Feishu Bridge 2.0 product boundary

Status: Working Draft

## Product promise

A macOS user who already uses Codex Desktop can install one local App, connect a Feishu custom app owned by their own tenant, and continue permitted Codex Tasks from Feishu without routing Codex content through a third-party bridge server.

## First-release architecture

The first public product uses BYOA (bring your own app):

- one Mac runs one local bridge;
- the Mac owner creates or selects a Feishu custom app in their own tenant;
- the App ID identifies that Feishu app;
- the App Secret is sent to the bundled `lark-cli` through stdin and stored by `lark-cli` in macOS Keychain;
- `config.json` stores only the Profile name, local authorization rules, menu keys, and non-secret runtime settings;
- Feishu messages, attachments, Codex Task state, queues, logs, and diagnostics remain on that Mac;
- no Roger, DeepOri, or shared cloud relay is required for the general bridge.

P0 supports the China Feishu environment. Lark international tenancy is a later compatibility target and is not presented as verified in the first-release assistant.

This avoids a shared multi-tenant service, central credential custody, cross-tenant routing, and cloud retention in the first release. A hosted shared-app edition would be a different product with separate security, privacy, operations, and compliance requirements.

## Product layers

### General product

- install, update, start, stop, diagnose, recover, and uninstall;
- connect the user's Feishu custom app;
- discover and approve Feishu users with explicit project rules;
- select, continue, create, archive, and restore Codex Tasks;
- transmit text and supported attachments;
- display progress, approvals, results, and Codex usage.

### Optional extensions

Workflow notifications are an extension surface. They must be disabled by default, absent from first-run setup, and unable to affect the general bridge when unconfigured.

### Private deployments

The current Ori One workflow contract, fixed workflow ID, private workbench URL, dedicated Task routing, and related recovery text belong to a private extension. They are not part of the general product promise.

## P0 acceptance criteria

1. A clean macOS 13+ account with Codex Desktop can install the Universal App without cloning the repository.
2. First launch explains that the bridge is local and that the user must own or create a Feishu custom app.
3. The user can enter App ID and App Secret in the App. The secret never appears in process arguments, `config.json`, logs, diagnostics, or crash text.
4. The App can confirm Profile, bot identity, Feishu endpoint, and Codex Desktop readiness separately.
5. The App shows the exact permissions, events, card callback, menu keys, and publication steps required in the Feishu console.
6. The first Feishu user can send one Bot message, be discovered by `open_id`, and receive no Task access until the Mac owner assigns explicit projects.
7. A connection test proves all three event consumers and one real Bot round trip. A passing local self-test alone is not success.
8. Updates preserve config and Task state, refuse to interrupt pending work, and verify version, SHA-256, bundle identity, signature, and both architectures.
9. Uninstall can remove the service while preserving local state. A separate explicit purge removes local bridge state and credentials.
10. Support diagnostics are useful without printing App Secret, user IDs, Chat IDs, Task IDs, message contents, or private paths beyond the current user's standard bridge locations.

## Release stages

- `2.0 alpha`: general/private boundary, first-connection assistant, safe Profile setup, and local validation.
- `2.0 beta`: first-user discovery, project picker, uninstall/recovery, migration, and tests on a second clean Mac and a second Feishu tenant.
- `2.0`: public documentation, version/release automation, signed and notarized package when an Apple Developer identity is available.

Until notarization is available, Releases must say that the App is ad-hoc signed and requires Finder -> right-click -> Open on first launch. The product must never tell users to disable Gatekeeper or remove quarantine attributes.
