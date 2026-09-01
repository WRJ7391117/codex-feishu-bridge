# DeepOri Bridge 2.0 test matrix

Status: Working Draft

Updated: 2026-09-01

| Surface | Target | Current evidence | Status |
| --- | --- | --- | --- |
| Source tests | isolated temporary HOME on macOS arm64 | 293 Python tests pass without the developer Codex database | Pass |
| Result audio | normal turn, Desktop sync, and Task subscription | explicit local audio links are separated from files; native Opus and attachment fallback plus durable retry are tested | Pass locally; real Feishu playback pending |
| Result-file authorization | selected Task workspace | local image/audio/file links outside the Task root are rejected; current-turn image-generation events remain separately scoped | Pass locally |
| State concurrency | App plus background process | a real subprocess waits on the shared state lock and preserves unrelated queue data while removing an access request | Pass locally |
| Event durability | message/card/menu dispatch | failed dispatch is not acknowledged; processed-event history retains seven days/up to 10,000 IDs | Pass locally |
| Public installer | GitHub latest Release | source tests cover digest, identity, signature, Universal architecture, downgrade, and rollback requirements | Pass locally; real clean-Mac install pending |
| App compiler | macOS 13 deployment target | Swift build passes | Pass |
| Codex-assisted setup | bundled local Skill, copyable prompt, no Feishu plugin or secret in Task | source, Skill, and package tests pass | Pass |
| Package | arm64 + x86_64 | Universal Mach-O verified by `lipo` | Pass |
| Package integrity | ZIP | generated SHA-256 manifest verifies from `dist/` | Pass |
| Sparkle integration | macOS App bundle | pinned 2.9.6 framework, Universal binary, rpath, signed appcast generation, and idle-install callbacks | Pass locally; real upgrade pending |
| Code signature | local build | deep/strict verification passes; signature is ad-hoc | Pass with limitation |
| Public/private boundary | general App payload | private workflow components are absent from the App, installer inputs, example config, main README, and public skill | Pass |
| First connection | existing Roger tenant/profile | code and static tests only; current App UI capture unavailable | Not accepted |
| First connection | new Feishu custom app | requires a second app/tenant and real Bot round trip | Not tested |
| First user discovery | bounded message listener | implementation and tests pass; real new-app event not yet exercised | Not accepted |
| Project authorization | Codex sidebar project picker | implementation and static tests pass | Not accepted visually |
| Runtime | current Mac existing install | 1.9.22 installed; service on with three consumers and no pending work after restart | Pass locally |
| Apple Silicon clean account | macOS 13+ | no clean-account install evidence yet | Not tested |
| Intel Mac | macOS 13+ | x86_64 binary exists; no real Intel launch evidence | Not tested |
| Gatekeeper | ad-hoc Release download | documented Finder right-click Open path | Not tested on another Mac |
| Signed/notarized package | Developer ID | build path exists; no paid Developer ID credentials | Blocked by credential |
| Keep-data uninstall | isolated temporary home | runtime removed; config and state preserved | Pass |
| Full purge | isolated temporary home | cancellation before change is tested; real Profile purge is not exercised | Partial |
| GitHub CI | macOS runner | clean-HOME local reproduction passes; remote run requires push | Pending |
| GitHub tag Release | public repository | tag/version gate and artifact upload workflow present | Pending |

## Public beta exit checks

1. Install the Release ZIP on a second Apple Silicon Mac account that has Codex Desktop but no bridge files.
2. Create a separate Feishu custom app with no Roger/DeepOri credentials.
3. Complete the first-connection assistant, permissions, events, callback, menu publication, first-user discovery, and explicit project selection.
4. Prove text, image, native Opus audio, fallback audio attachment, file, progress card, one card action, final reply, and current-Task persistence with a real round trip.
5. Stop/start, reboot, keep-data uninstall, reinstall, and state recovery.
6. Repeat the install/launch/stop/recover subset on an Intel Mac or a real x86_64 macOS environment.
7. Push a versioned commit, observe CI, create the matching tag, and verify the published ZIP, SHA-256, `update.json`, signed `appcast.xml`, and in-App update path.

No code-only or local-source check substitutes for the new Feishu app and second-Mac acceptance above.

The second clean Mac and separate Feishu app/tenant checks are deliberately deferred until the hardware is procured. They remain release gates; they are not recorded as passed from the current Mac.
