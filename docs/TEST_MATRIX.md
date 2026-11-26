# DeepOri Bridge test matrix

Status: `1.11.21 (build 101)` pre-release verification

Updated: 2026-11-26

“Pass locally” below means source tests or bounded local protocol checks passed. It does not mean the candidate was published, installed, or accepted through a real Feishu round trip.

| Surface | Target | Current evidence | Status |
| --- | --- | --- | --- |
| Source regression | isolated temporary HOME on macOS arm64 | all 540 Python tests pass; Python 3.9 product import and Shell syntax checks pass; independent review findings have been repaired and covered by regressions | Pass locally |
| Shared app-server transport | current managed local daemon | Unix/WebSocket initialize and same-Task resume work against the real shared daemon; bounded two-client, approval, and stop checks passed without using the protected App Tools pipe | Pass locally; current installed runtime not migrated |
| Same-Task integrity | existing Codex Task | source and protocol checks retain the original Task ID and guard uncertain acknowledgements from replay | Pass locally; real Feishu E2E pending |
| Event observation | subscribed Desktop Task | the shared connection consumes app-server events and uses state-file events to refresh targets; no Task-storage polling was added to the new path | Pass locally; restart E2E pending |
| Paginated history | database and legacy JSONL | authoritative backend selection, stale-JSONL exclusion, subscription migration, and legacy fallback have regression coverage | Pass locally |
| Input and output | text, image, audio, and file | event-driven input mapping and durable result-delivery paths have source coverage | Pass locally; real Feishu media round trip pending |
| Approval and stop | one-time approval and active turn interrupt | real shared-daemon protocol checks passed; Feishu card-to-daemon-to-card acceptance has not been run on the candidate | Pass protocol check; Feishu E2E pending |
| State concurrency | App plus background process | state locking and durable queue behavior have regression coverage | Pass locally |
| Event durability | message/card/menu dispatch | failed dispatch is not acknowledged; persisted retries do not rerun Codex | Pass locally |
| Runtime module manifest | install, update, rollback, and uninstall | `codex_app_server.py` and `codex_event_observer.py` are included in installer preflight/staging, runtime backup/restore, uninstall, and packaging fixtures | Pass locally |
| Automatic update discovery | Sparkle 2.9.6 | checks are event-driven at App launch, new Bridge activity, and App activation/reopen; activation is rate-limited and guarded against overlapping sessions; no scheduled update timer is configured | Pass locally; real `1.11.20` to `1.11.21` upgrade pending |
| Idle update installation | active runs, queues, and pending delivery | existing quiesce, nonce acknowledgement, safe-install, and rollback paths remain covered | Pass locally; candidate install pending |
| App compiler | macOS 13 deployment target | the current host reaches Swift compilation but its Command Line Tools lack the `SwiftUIMacros` plugin used by existing SwiftUI `@State` declarations | Blocked locally; CI required |
| Universal package | arm64 + x86_64 | release workflow builds the App, bundled `lark-cli`, PromLight helper, and Sparkle framework for both architectures | Candidate package not built |
| Package integrity | ZIP, SHA-256, and appcast | workflow verifies the archive digest, signed appcast XML, version/build, download URL, length, and Sparkle signature | Candidate assets pending |
| Code signature | current Release path | workflow requires deep/strict verification and an ad-hoc signature; Developer ID notarization is outside the current release contract | Pending candidate CI |
| Installed App | current Mac | installed App is `1.11.20 (build 100)`; source is `1.11.21 (build 101)` | Not synchronized |
| Installed runtime | current Mac | installed `bridge.py` differs from the `1.11.21` candidate; the running Desktop/Bridge path has not migrated to the new shared observer | Not synchronized |
| Real Feishu acceptance | authorized user and real Bot | no post-change Feishu message, approval, stop, final reply, or media round trip has been accepted on `1.11.21` | Not tested |
| Public/private boundary | release payload | private workflow components remain excluded from the public App payload and example configuration | Pass locally |
| Apple Silicon clean account | macOS 13+ | no clean-account install evidence for this candidate | Not tested |
| Intel Mac | macOS 13+ | CI can build x86_64; no real Intel launch evidence | Not tested |
| Gatekeeper | ad-hoc Release download | documented Finder right-click Open path | Not tested on another Mac |
| GitHub CI | macOS runner | `v1.11.20` workflows passed; `1.11.21` has not run on CI | Pending |
| GitHub tag and Release | public repository | tag/version immutability gates and four-asset publication workflow are present; `v1.11.21` is not published | Pending |

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
