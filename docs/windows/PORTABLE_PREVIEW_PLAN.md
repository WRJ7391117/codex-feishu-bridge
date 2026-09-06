# Windows portable preview: deployment and trust decision

Status (2026-09-06): user explicitly accepted the unsigned portable trust boundary.
Implementation and local validation are in progress. No public portable download
has been published. Acceptance of the design is not clean-machine acceptance.

## Intended distribution

An explicitly unsigned, Windows 11 x64 **prerelease**, delivered as a ZIP through
the project's GitHub Releases. Bundle the .NET and Windows App SDK runtimes after
checking their redistribution requirements. Do not require Git, Go, Visual
Studio, Developer Mode, certificate installation, or disabling Windows security.
Codex Desktop and the user's own Feishu application remain prerequisites.

Microsoft documents unpackaged/self-contained deployment here:
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps

## Confirmed code dependencies

| Module | Current dependency | Required portable work |
| --- | --- | --- |
| `DeepOriBridge.csproj` | MSIX tooling; developer identity bound to build output | Separate explicit publish profile, self-contained dependencies, no developer permissions |
| `BridgeProductIdentity.cs` | Exact package family/publisher/AUMID; loose developer path only | Separate portable identity policy; retain canonical-path, payload, PID/start-time and window checks |
| `BridgeSetupHandshakeService.cs` and packaged setup Skill | Handshake accepts the packaged local-preview contract | Separate versioned portable contract and tests; do not relabel portable as packaged or signed |
| `CurrentTaskSelectionStore.cs` | `ApplicationData.Current.LocalSettings` | Deployment-neutral settings provider with atomic writes and per-user isolation |
| `PromLightSettingsStore.cs`, `PromLightFleetService.cs`, `PromLightService.cs` | Package-local settings and paths | Portable per-user storage preserving encrypted ownership and cross-process locking |
| `BridgeCrashRecovery.cs`, `Watch-BridgeProcess.ps1` | Package storage and package activation | Exact executable recovery; original PID/start-time, normal-exit marker, bounded attempts, payload verification |
| `FeishuCredentialService.cs` | Windows Credential Locker plus development/production namespaces | Verify real unpackaged credential operations; separate preview namespace; no automatic credential migration |

The above is the confirmed initial impact, not a claim that all package-dependent
calls have been exhaustively eliminated. Repeat the repository-wide scan after
implementation, including startup, XAML code-behind and generated prompts.

## Trust decision needed before enabling sensitive runtime

An unsigned executable cannot claim Windows-verified publisher identity. A hash
file delivered beside the executable detects corruption but does not independently
prove publisher identity when both files can be replaced.

Proposed preview contract: users explicitly trust the downloaded GitHub release
on first use; record consent tied to the version/payload; reject subsequent
payload changes and require confirmation for updates. Explain that this does not
provide the signed MSIX trust boundary or protection against an attacker already
controlling the same Windows account. Keep credentials out of the ZIP and source.

The user confirmed this changed trust boundary before implementation. Do not simply return true from
`CanUseSensitiveRuntime`, enable the developer flags, accept arbitrary package
identities, or reuse the existing local-preview credential namespace.

## Implementation order after decision

1. Define portable channel, trust/consent and credential-isolation contracts;
   add rejection tests for tampering, stale consent, changed path and wrong process.
2. Introduce deployment-neutral storage; retain packaged behavior unchanged;
   test missing/corrupt storage, concurrent writes and upgrade rollback.
3. Implement portable startup, exact-process recovery and setup handshake;
   keep project authorization, queues and multi-owner lamp rules unchanged.
4. Build into an isolated publish directory; generate an allowlisted ZIP and
   SHA-256 manifest from build output, never from a running development directory.
5. Verify relocation to paths with spaces and non-ASCII characters; run without
   package registration and without developer tools/runtime already installed.
6. Verify actual Credential Locker operations in that model, then text/image/file
   round trips, queueing, result delivery, reconnect and normal/crash restart.
7. Validate clean-user first run, upgrade, rollback and uninstall. Removing program
   files must not silently delete credentials or user data.
8. Only publish a prerelease after recording the exact source SHA, CI results,
   artifact hashes and completed acceptance evidence. No `latest` or stable tag.

## Acceptance still external to this development host

Clean Windows VM/user verification and real two-PromLight/two-authorized-user
acceptance remain outstanding. Unit-test success does not substitute for these.
The existing signed-release gates remain unchanged; this document does not waive
them or authorize distributing the current AppX directory.

## Local implementation evidence

- 1,527 protocol/safety assertions passed, including 22 new portable cases.
- Windows PowerShell 5.1 setup examples passed without live remote writes.
- A real unpackaged Credential Locker probe added, read and removed one synthetic
  resource in an isolated probe namespace. Existing credentials were not enumerated.
  This is narrower than the skipped comprehensive Credential Locker integration suite.
- A self-contained build extracted to a different Unicode/space path opened the
  first-connection page, including when its working directory differed from its executable.
- Attempting write access to a running payload DLL was denied without modifying it.
- Forced termination of the isolated empty preview was followed by a responsive
  replacement window and a `window-restored` recovery record. Normal close then
  exited without leaving its watcher or restarting the App.
- Missing PRI/image assets found during launch testing now fail the packaging
  preflight. Runtime files, notices and installation instructions are included in
  the integrity manifest; personal state remains outside the archive.

No real Feishu credentials were configured in this isolated preview. Portable
automatic-configuration, real bot round trips, hardware parity and clean-machine
acceptance remain unverified. No public release was created by these checks.
