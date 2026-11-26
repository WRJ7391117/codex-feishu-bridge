# 2026-11-26 compatibility repair

Scope: restore the Feishu → original Codex Task → Feishu path for the current Desktop generation. This document records candidate evidence and remaining release gates; it does not claim that `1.11.21` is published or installed.

Candidate identity: `1.11.21 (build 101)`.

## Confirmed causes

- The current Desktop generation no longer listens on the legacy IPC socket. A stale socket path or a running App process is not proof of a listening endpoint.
- A separate app-server process cannot safely stand in for the Desktop-owned Task. Bridge must connect through the official app-server proxy transport to the same managed local daemon.
- The protected App Tools pipe is not a public Bridge endpoint and is not used.
- Current Task history may be authoritative in paginated SQLite even when an older rollout JSONL still exists. Selecting history by file presence can therefore freeze completion and subscription state on stale data.
- Repeated catalog reads amplified intermittent SQLite open failures. Catalog lookup now uses bounded retries/backoff and shared history membership instead of opening history per Task.

## Implemented candidate behavior

- The shared Unix transport performs a WebSocket upgrade, initializes the public app-server protocol, keeps responses and events on separate paths, and never retries a possibly submitted write.
- Bridge resumes the original Task through the managed daemon and follows `turn/started`, item, approval, stop, and `turn/completed` events.
- A long-lived shared observer follows subscribed Tasks. State-file events refresh its target set; reconnect performs bounded reconciliation instead of adding a new Task-storage polling loop.
- Database-backed and legacy JSONL history use an explicit authoritative backend, including migration of existing Task subscriptions.
- `codex_app_server.py` and `codex_event_observer.py` are present in installer preflight/staging, runtime backup/restore, uninstall, and test fixtures.
- Sparkle update discovery remains event-driven. In addition to launch and real Bridge activity, App activation or reopen requests a background check with a 60-second local rate limit and overlapping-session guard.

## Evidence completed

- [x] Real local shared-daemon initialize and same-Task resume through Unix/WebSocket.
- [x] Bounded two-client shared-daemon observation on the same managed instance.
- [x] Bounded shared-protocol one-time approval and active-turn stop checks.
- [x] A second real client resumes the same Task after its turn starts and receives the same completion. The real Bridge runner accepts explicit model/effort settings; `serviceTier: null` clears priority to default on readback. Directory, approval/reviewer policy, and read-only/network permissions remain unchanged. Disposable test Tasks were archived; this check used no tools or Feishu messages.
- [x] Focused transport, observer, backend, history migration, installer-manifest, and update-activation regression tests.
- [x] Final full suite: 540 tests pass with isolated HOME. System Python 3.9 product import, Shell syntax, and diff checks pass.
- [x] Independent integration review findings repaired: handoff-only targets, all-user reconciliation, fast terminal events, partial subscription failures, ownership races, pending-history recovery, supported settings parameters, and resolved approval cards.
- [x] Transactional install/update module lists include both new Python modules and retain rollback/uninstall coverage.
- [x] Candidate version/build are unique relative to the installed and published `1.11.20 (build 100)`.

These checks establish the local protocol and source behavior. They are not a real Feishu acceptance run.

## Explicitly not yet verified

- [ ] The currently installed App is still `1.11.20 (build 100)` and its installed runtime differs from the candidate. The current Desktop/Bridge process has not migrated to the new shared observer.
- [ ] No real Feishu message has traversed the `1.11.21` candidate from Bot ingress through the original Desktop Task to the final Bot reply.
- [ ] Approval, stop, media delivery, restart recovery, and duplicate suppression have not been accepted end to end through real Feishu on the installed candidate.
- [ ] The current host cannot complete the Swift build because its Command Line Tools lack the existing SwiftUI macro plugin; the standard macOS CI build is required.
- [ ] No `1.11.21` Universal package, CI run, tag, GitHub Release, signed appcast, or installed-runtime verification exists yet.
- [ ] Sparkle discovery and installation from the installed `1.11.20` App to `1.11.21` have not been exercised.

## Release acceptance checklist

- [x] Final full regression and independent source review pass.
- [ ] macOS CI builds the Universal App and verifies ad-hoc deep signature, both architectures, Sparkle linkage, archive digest, and appcast XML.
- [ ] Release contains exactly the ZIP, SHA-256 file, `update.json`, and signed `appcast.xml`, all matching `1.11.21 (build 101)` and tag `v1.11.21`.
- [ ] Sparkle upgrades the installed `1.11.20` App without interrupting active Bridge work.
- [ ] Installed App and runtime both match `1.11.21`; LaunchAgent and three event consumers are healthy after migration.
- [ ] Real Feishu E2E proves the original Task is reused and covers final reply, one-time approval, stop, and representative attachments without duplicate execution.
- [ ] Restart recovery and result-delivery retries do not rerun Codex.

Desktop live visibility and ownership must be checked against the migrated shared connection. They are not inferred from an independent CLI run, source tests, or the current unmodified Desktop process.

The Sparkle gate must be exercised before any fallback installation: keep the existing `1.11.20` bundle in place, verify the published appcast independently, make Bridge idle, and relaunch the old App so its startup hook performs discovery and download. If automatic discovery fails, the manual “检查更新” action may be used to expose the Sparkle error. Replacing the bundle with a downloaded ZIP or running the fallback installer can recover the installation, but it cannot prove that automatic update discovery or installation works.
