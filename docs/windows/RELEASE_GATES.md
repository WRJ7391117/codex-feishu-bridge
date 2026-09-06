# Windows release gates

Status: **not approved for a signed Windows release**.

The maintainer does not currently have a Windows code-signing service. Source
publication remains possible; this is not a blanket requirement that all Windows
executables be signed. This project's present packaged identity and sensitive
runtime checks do require the proper release identity for a distributable
installer. An unsigned portable edition would be a separate packaging change,
not a renamed development AppX directory.

The current local App is a development preview. Do not redistribute its AppX
directory as a portable app: its local identity attestation is tied to the build
location. Do not import a self-signed certificate into users' trust stores as an
automatic workaround.

## Source candidate

`windows-ci.yml` builds a candidate with `BridgeDeveloperBuild=false` and
`BridgeLocalPreviewSensitiveRuntime=false`, runs protocol/Go tests and checks
the PowerShell 5.1 setup examples. It never signs or publishes a binary. A green
CI result is not a product acceptance result.

The Go sidecar and its hash manifest are generated in each project's intermediate
directory, not into shared source files. Protocol tests build and validate their
own sidecar. User credentials, real Task histories and runtime files must not be
included in source commits or release assets.

## Required before installation-package release

1. Freeze an audited source SHA and record the corresponding CI run.
2. Configure a stable publisher identity and a legitimate Windows signing
   certificate/service. The maintainer currently has no signing service.
3. Build the MSIX with development and unsigned-sensitive-runtime flags disabled.
4. Verify package signature, identity, dependencies and SHA-256.
5. On a clean Windows user/VM, verify installation, setup, upgrade, uninstall and
   reinstall without trusting development credentials.
6. Verify a real Feishu text/image/file round trip and a published menu click with
   its business response, including cancellation and reconnect.
7. With at least two physical PromLights and two authorized Feishu users, verify
   independent Task state, ownership, disconnect/reconnect, default selection,
   pending unbind ACK, restart and old-card rejection.
8. Verify keyboard navigation, Narrator, high contrast, dark mode and text scaling.

Do not mark these checks complete merely because unit tests pass. Until they pass,
publish source only or retain an explicitly unreleased draft; no stable Windows
binary release, `latest` designation or macOS release tag should be created.

## Multi-lamp migration

The legacy single-lamp settings are preserved. An existing exact pinned endpoint
and its subscriptions migrate atomically only when there is exactly one verified
authorized user and every old Task remains in that user's authorized project.
Ambiguous ownership or missing identity is not guessed. New bindings require an explicit
physical endpoint and an authorized owner. A device already owned or waiting for
idle-write acknowledgement cannot be assigned to another owner. Display names
are not device identities. Revoked owners have their lamps placed into cleanup,
and offline cleanup retains the reservation.

Configuration changes and HID writes share an exact per-lamp lane, including a
cross-process exclusive file lease. While a write
is outstanding, synchronous settings actions report busy without committing a
change. Once submitted to Windows, a write retains its lane until the actual OS
operation completes; cancellation does not pretend to revoke a physical frame.
An unresponsive driver can therefore leave that lamp busy, without blocking the
other lamps. No successful unbind or physical LED verification is reported then.
Malformed or unknown-version v1 settings are refused, not silently imported as an
empty lamp. Adding/selecting an authorized user preserves other users' project
scopes; explicit owner revocation still invalidates the old project generation.

Local candidate evidence (2026-09-06): 1,505 protocol/safety assertions passed,
Go tests passed, and the Release build with both development permissions disabled
reported zero warnings/errors. Windows PowerShell 5.1 setup examples passed.
Four pages at four widths passed 16 read-only UI checks. Regression coverage
includes an independent child process holding a lamp lease and corrupt/ACL-denied
owner storage not preventing other users' subscription observation.
These are automated checks, not a signing or multi-device acceptance report.
The actual Credential Locker integration checks were skipped because the host
broker could not be exercised; installed-user Skill inspection was skipped in
the isolated run while packaged Skill integrity remained checked. Do not treat
the newly added multi-owner Credential Locker scenarios as executed.

Input attachments now have encrypted session ownership markers. Exact terminal
turns allow cleanup; orphan cleanup requires seven days, a dead owner process,
no active queue claim, and a non-running Task. Unmarked legacy directories are
not automatically removed because ownership cannot be proved safely.

Workflow action inputs follow the official repositories:
[setup-dotnet](https://github.com/actions/setup-dotnet),
[setup-go](https://github.com/actions/setup-go).
