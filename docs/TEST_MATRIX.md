# DeepOri Bridge test matrix

Status: `1.11.21 (build 101)` published and installed; Desktop restart and real Feishu acceptance pending

Updated: 2026-11-27

"Pass locally" means source or bounded local protocol evidence. Publication and installation are recorded separately; real Feishu acceptance remains pending.

| Surface | Target | Current evidence | Status |
| --- | --- | --- | --- |
| Source regression | isolated temporary HOME on macOS arm64 | all 540 Python tests pass; Python 3.9 product import and Shell syntax checks pass; independent review findings have been repaired and covered by regressions | Pass locally |
| Shared app-server transport | current managed local daemon | real two-client observation, joining while a turn is running, one-time approval, stop, and model/effort/tier readback passed; installed runtime has a public proxy connection and passes shared-service diagnosis | Pass protocol and runtime; old Desktop process still needs restart |
| Same-Task integrity | existing Codex Task | source and protocol checks retain the original Task ID and guard uncertain acknowledgements from replay | Pass locally; real Feishu E2E pending |
| Event observation | subscribed Desktop Task | the shared connection consumes app-server events and uses state-file events to refresh targets; no Task-storage polling was added to the new path | Pass locally; restart E2E pending |
| Paginated history | database and legacy JSONL | authoritative backend selection, stale-JSONL exclusion, subscription migration, and legacy fallback have regression coverage | Pass locally |
| Input and output | text, image, audio, and file | event-driven input mapping and durable result-delivery paths have source coverage | Pass locally; real Feishu media round trip pending |
| Approval and stop | one-time approval and active turn interrupt | real shared-daemon protocol checks passed; Feishu card-to-daemon-to-card acceptance has not been run on the candidate | Pass protocol check; Feishu E2E pending |
| State concurrency | App plus background process | state locking and durable queue behavior have regression coverage | Pass locally |
| Event durability | message/card/menu dispatch | failed dispatch is not acknowledged; persisted retries do not rerun Codex | Pass locally |
| Runtime module manifest | install, update, rollback, and uninstall | `codex_app_server.py` and `codex_event_observer.py` are included in installer preflight/staging, runtime backup/restore, uninstall, and packaging fixtures | Pass locally |
| Automatic update discovery | Sparkle 2.9.6 | safe relaunch of the installed `1.11.20` App automatically discovered, downloaded, and replaced it with `1.11.21`; no manual update click or bundle copy was used | Pass actual upgrade |
| Idle update installation | active runs, queues, and pending delivery | actual idle Sparkle replacement synchronized runtime; LaunchAgent is running with three fresh event-ready markers; config digest stayed unchanged | Pass idle install; active-work deferral remains source-tested |
| App compiler | macOS 13 deployment target | standard macOS CI type-check and release builds pass; the local Command Line Tools macro limitation remains host-specific | Pass CI |
| Universal package | arm64 + x86_64 | independent audit verifies both architectures for App, bundled CLI, PromLight helper, and Sparkle framework | Pass released package |
| Package integrity | ZIP, SHA-256, and appcast | all four assets match GitHub digests; appcast version/build/tag URL/length match; Ed25519 verifies with the installed old App's public key | Pass released assets |
| Code signature | current Release path | released and installed App pass deep/strict ad-hoc verification; no Developer ID notarization is claimed | Pass current contract |
| Installed App | current Mac | `1.11.21 (build 101)` binary and bundled runtime files match the published archive | Synchronized |
| Installed runtime | current Mac | `bridge.py` and both public modules match reviewed source; three consumers, LaunchAgent, and shared-service diagnosis pass; two authorized users and config are preserved | Synchronized; Desktop restart pending |
| Real Feishu acceptance | authorized user and real Bot | no post-change Feishu message, approval, stop, final reply, or media round trip has been accepted on `1.11.21` | Not tested |
| Public/private boundary | release payload | private workflow components remain excluded from the public App payload and example configuration | Pass locally |
| Apple Silicon clean account | macOS 13+ | no clean-account install evidence for this candidate | Not tested |
| Intel Mac | macOS 13+ | CI can build x86_64; no real Intel launch evidence | Not tested |
| Gatekeeper | ad-hoc Release download | documented Finder right-click Open path | Not tested on another Mac |
| GitHub CI | macOS runner | commit `05ec7a5` passed CI run `45842707782` and Release run `45906753388` | Pass |
| GitHub tag and Release | public repository | immutable `v1.11.21` points to `05ec7a5`; four audited assets are published | Published |

## `1.11.21` release gates

1. Completed locally: 540 Python tests, Python 3.9 product import, Shell syntax checks, and independent source review.
2. Push the reviewed versioned commit and let the macOS CI build and verify the Universal App.
3. Create only the matching immutable `v1.11.21` tag; do not replace an existing Release or signed asset.
4. Verify the published ZIP, SHA-256 file, `update.json`, and signed `appcast.xml` all identify `1.11.21 (build 101)` and the immutable tag URL.
5. Leave the installed `1.11.20` bundle intact, make the Bridge idle, and relaunch that App so its startup hook must discover, download, and install `1.11.21` through Sparkle. Use the manual “检查更新” action only to expose a Sparkle error if automatic discovery fails; do not use a fallback installer before this evidence is captured.
6. After Sparkle relaunches the App, verify App/runtime parity, rollback safety, three event consumers, and shared-daemon observer health.
7. Run a real Feishu E2E on the migrated runtime: same existing Task, final reply, one-time approval, stop, and representative image/audio/file delivery without duplicate execution.
8. Keep clean-account, second-tenant, Intel, and Gatekeeper checks as separate public-beta exit evidence; do not infer them from CI or the current Mac.

No source test, local protocol check, or successful package build substitutes for installing the candidate and completing the real Feishu E2E.
