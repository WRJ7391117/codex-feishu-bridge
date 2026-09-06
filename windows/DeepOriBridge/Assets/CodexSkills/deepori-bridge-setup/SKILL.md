---
name: deepori-bridge-setup
description: Set up DeepOri Bridge for the first time on Windows 11 by coordinating the local App, Codex Desktop, and a Feishu custom Bot.
---

# DeepOri Bridge Setup for Windows

Configure DeepOri Bridge on this Windows 11 computer. The target is the one Bot identity embedded in the App-generated prompt, with one explicitly authorized Feishu owner and only explicitly selected Codex project access.

## Boundaries

> **Local preview boundary:** Codex-assisted setup is available only from the exact running Bridge window that issued the prompt's 120-second, single-use named-pipe challenge. The pipe is identity verification only; configuration continues through the verified Bridge UI and the explicit human gates below.

- Never discover the target with `Get-AppxPackage | Where Name/PackageFullName -match DeepOri`, `Select-Object -First 1`, fuzzy titles, result order, or any other broad DeepOri match.
- `DeepOri.AgentMesh`, `DeepOriAgentMesh.exe`, and pages containing “初始化本机节点” or “启动本地服务” are the separate DeepOri Agent Mesh product. Stop immediately if any appear. Do not initialize, start, stop, delete, or roll back Mesh data.
- A packaged Bridge candidate is valid only when the same observed candidate matches executable `DeepOriBridge.exe`, product `DeepOri Bridge for Windows`, package name `E1E677B4-5C45-4603-818D-FA1592843ABC`, family `E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6`, Publisher `CN=AppPublisher`, PublisherId `1z32rh13vfry6`, ApplicationId `App`, AUMID `E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6!App`, canonical package path, apphost SHA-256, security-critical payload manifest SHA-256, exact PID/start time, one process-owned top-level HWND, exact title, and one exact whitelisted page AutomationId. Zero or multiple process/window candidates fail closed. Prefix-only UIA names never qualify. Publisher is still a development placeholder and remains a release Gate.
- An unsigned local-preview build is accepted only when build metadata explicitly enables the preview, its canonical path is the fixed packaged `AppX` output, and it uses `DeepOriBridge.Windows.Development.Unsigned.Isolated` Credential Locker resources. It must never read, migrate, or reuse production or Agent Mesh credentials. An arbitrary same-name/ProductName executable never qualifies.
- Before any coordination, require the App-issued 120-second, single-use nonce over the named pipe named in the same prompt. Verify PID, process start time, apphost SHA-256, payload manifest SHA-256, HWND, exact UIA page, and the exact echoed nonce. A stale, replayed, missing, or different value fails closed. The pipe operation is only `verify-identity-readonly`; it never directly mutates App state.
- After the handshake succeeds, re-check the same PID/start time/HWND/title and exact page before every UI action. You may operate only the verified Bridge UI. If the window, page, process, payload digest, or package identity changes, stop and ask the user to start again from the App. Never enumerate a broad DeepOri candidate list.
- While the App displays “Codex 自动配置进行中”, treat its other setup paths as locked. Do not ask the user to return to the manual wizard or manipulate a control unless the App explicitly presents it as the current human Gate. At a Gate, name only the one enabled control or external confirmation that is required. Exiting automatic takeover revokes the current nonce.
- Existing credentials do not prove App identity. Require a verified identity generation derived from the official `/open-apis/bot/v3/info` bot `open_id` and `app_name`, and require that exact fingerprint before any long connection.
- Treat the App ID, Bot name, identity fingerprint, and allowed Feishu URL prefix appended by Bridge as one indivisible target. Re-check all available values before every browser or API write. A mismatch, missing value, or different application page fails closed; never select an application by fuzzy name or result order.
- After identity verification, let Bridge automatically read the current application/version and compare the four managed menu groups using Feishu's floating-menu display form: `Task 管理`, `桌面 Task`, `模型设置`, and `提示灯`. All 12 Event Keys listed below have Windows card and callback handlers and must be included; a missing, renamed, merged, or reordered item is not acceptable. The exact remote diff must explicitly identify the floating display form. Never treat the three registered consumers as menu completion. Revalidate the exact Bot/tenant identity generation before every remote write, and stop on an unknown custom menu instead of overwriting it.
- A remote menu containing an unknown Event Key, redirect, multiple candidate application, or changed identity is a fail-closed custom conflict. Never overwrite it. Manual entry of the 12 known keys is an exact diagnostic fallback only, never the normal workflow.
- PromLight is optional hardware. Include `promlight` and `promlight_legend` even when no device is connected; do not block Bridge setup on HID discovery. Report physical light verification separately from menu availability.

- DeepOri Bridge and Codex Desktop run on this Windows computer; Codex does not run inside Feishu.
- Do not install a Feishu or Lark plugin for Codex.
- Never ask for an App Secret in a Codex Task, command line, file, state, or log. The user enters it only in the Bridge PasswordBox, and the App stores it in the current Windows user's Credential Locker.
- Never grant wildcard or all-project access. Require an explicit project selection.
- Clicking Bridge's “授权自动配置并打开 Codex” or “授权并复制指令” after verified credential entry authorizes only the declared Bridge scopes/events, managed menu, and matching version submission. Do not ask for duplicate authorization. Unexpected changes, unknown menus, owner binding, and project selection remain separate gates.
- Do not copy or commit credentials, user IDs, chat IDs, Task IDs, configuration, state databases, or logs.

## Identity Handshake

Run the handshake immediately after reading this skill. Replace the three angle-bracket placeholders only with the exact values from the App-generated prompt. Use a persistent Windows PowerShell 5.1 process (the OS Appx module is supported there), keeping its stdin/session alive for all later commands. Never launch a one-shot process for the handshake. If tools cannot keep that process alive, explain the limitation and offer the App's manual path rather than repeatedly consuming challenges. Do not discover another App or reuse an old challenge.

```powershell
$bridgePipe = '<PIPE>'
$bridgeNonce = '<NONCE>'
$bridgePayload = '<PAYLOAD_SHA256>'
$bridgeProcess = [Diagnostics.Process]::GetCurrentProcess()
$bridgeClientPath = $bridgeProcess.MainModule.FileName
$bridgeRequest = @{
    Nonce = $bridgeNonce
    Operation = 'verify-identity-readonly'
    ClientProcessId = $bridgeProcess.Id
    ClientProcessStartTimeUtcTicks = $bridgeProcess.StartTime.ToUniversalTime().Ticks
    ClientExecutablePath = $bridgeClientPath
    ClientExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $bridgeClientPath).Hash
    ExpectedBridgePayloadManifestSha256 = $bridgePayload
} | ConvertTo-Json -Compress
$bridgeClient = [IO.Pipes.NamedPipeClientStream]::new('.', $bridgePipe, [IO.Pipes.PipeDirection]::InOut)
$bridgeClient.Connect(5000)
$bridgeWriter = [IO.StreamWriter]::new($bridgeClient, [Text.UTF8Encoding]::new($false), 1024, $true)
$bridgeWriter.AutoFlush = $true
$bridgeReader = [IO.StreamReader]::new($bridgeClient, [Text.Encoding]::UTF8, $false, 1024, $true)
$bridgeWriter.WriteLine($bridgeRequest)
$bridgeResponse = $bridgeReader.ReadLine() | ConvertFrom-Json
$bridgeClient.Dispose()
if (-not $bridgeResponse.Accepted -or $bridgeResponse.Nonce -ne $bridgeNonce) {
    throw 'DeepOri Bridge identity handshake failed; stop setup.'
}
# Compare these fields with the prompt in memory; do not print raw identity evidence.
```

The returned PID, start time, payload hash, HWND, and page must exactly equal the prompt. Do not print the request or full response. If the challenge expires, tell the user to click the App's copy/open action again and use only the new prompt.

## Local Preview Attestation

An accepted handshake is not enough by itself. In the same PowerShell process, immediately validate the response with the deterministic checks below before any UI action. This is the only supported local-preview audit; do not invent additional source-tree or fuzzy package checks.

- Require `PackagingMode=packaged`, `BuildChannel=unsigned-local-preview`, `PackagePathAttested=true`, `DeveloperPathAttested=true`, `LocalPreviewSensitiveRuntimeEnabled=true`, `ProductionCredentialReuseEnabled=false`, `AgentMeshCredentialAccessEnabled=false`, `CredentialLockerIsolated=true`, and the exact resource prefix `DeepOriBridge.Windows.Development.Unsigned.Isolated` in the accepted response. The pipe server rejects the handshake unless these values are the runtime values used by the Bridge service graph.
- Query only the exact package name `E1E677B4-5C45-4603-818D-FA1592843ABC`. Require exactly one package, family `E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6`, Publisher `CN=AppPublisher`, ApplicationId `App`, and the response's exact package location/executable path.
- Require the response's payload file list to equal the following complete list in the following order; do not accept a caller-supplied subset: `AppxManifest.xml`, `Assets\CodexSkills\deepori-bridge-setup\.deepori-bridge-managed`, `Assets\CodexSkills\deepori-bridge-setup\SKILL.md`, `DeepOriBridge.deps.json`, `DeepOriBridge.dll`, `DeepOriBridge.exe`, `DeepOriBridge.runtimeconfig.json`, `Microsoft.WinUI.dll`, `resources.pri`, `Tools\feishu-event-sidecar.exe`, `Tools\feishu-event-sidecar.manifest.json`, `Tools\Watch-BridgeProcess.ps1`.
- Recompute the manifest exactly as UTF-8 lines of `forward/slash/path + NUL + byte length + NUL + uppercase SHA-256 + LF`, sorted with `StringComparer.Ordinal`; require its SHA-256 to equal both the prompt and response.

Use this continuation after `$bridgeResponse` has passed the nonce check:

```powershell
$expectedPackageName = 'E1E677B4-5C45-4603-818D-FA1592843ABC'
$expectedFamily = 'E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6'
$expectedVault = 'DeepOriBridge.Windows.Development.Unsigned.Isolated'
$expectedFiles = @(
    'AppxManifest.xml'
    'Assets\CodexSkills\deepori-bridge-setup\.deepori-bridge-managed'
    'Assets\CodexSkills\deepori-bridge-setup\SKILL.md'
    'DeepOriBridge.deps.json'
    'DeepOriBridge.dll'
    'DeepOriBridge.exe'
    'DeepOriBridge.runtimeconfig.json'
    'Microsoft.WinUI.dll'
    'resources.pri'
    'Tools\feishu-event-sidecar.exe'
    'Tools\feishu-event-sidecar.manifest.json'
    'Tools\Watch-BridgeProcess.ps1'
)
$packages = @(Get-AppxPackage -Name $expectedPackageName)
if ($packages.Count -ne 1) { throw 'Exact Bridge package cardinality failed.' }
$package = $packages[0]
$manifest = Get-AppxPackageManifest -Package $package
$apps = @($manifest.Package.Applications.Application)
$bridge = [Diagnostics.Process]::GetProcessById([int]$bridgeResponse.BridgeProcessId)
$root = [IO.Path]::GetFullPath($package.InstallLocation)
$exe = [IO.Path]::GetFullPath($bridgeResponse.BridgeExecutablePath)
if ($package.PackageFamilyName -cne $expectedFamily -or
    $package.Publisher -cne 'CN=AppPublisher' -or
    $apps.Count -ne 1 -or $apps[0].Id -cne 'App' -or
    $bridge.StartTime.ToUniversalTime().Ticks -ne $bridgeResponse.BridgeProcessStartTimeUtcTicks -or
    $root -cne [IO.Path]::GetFullPath($bridgeResponse.BridgePackageInstallLocation) -or
    $exe -cne [IO.Path]::Combine($root, 'DeepOriBridge.exe') -or
    $bridge.MainModule.FileName -cne $exe -or
    (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $bridgeResponse.BridgeExecutableSha256 -or
    $bridgeResponse.PackagingMode -cne 'packaged' -or
    $bridgeResponse.BuildChannel -cne 'unsigned-local-preview' -or
    -not $bridgeResponse.PackagePathAttested -or
    -not $bridgeResponse.DeveloperPathAttested -or
    -not $bridgeResponse.LocalPreviewSensitiveRuntimeEnabled -or
    $bridgeResponse.ProductionCredentialReuseEnabled -or
    $bridgeResponse.AgentMeshCredentialAccessEnabled -or
    -not $bridgeResponse.CredentialLockerIsolated -or
    $bridgeResponse.CredentialLockerResourcePrefix -cne $expectedVault -or
    [string]::Join("`n", $bridgeResponse.PayloadManifestFiles) -cne [string]::Join("`n", $expectedFiles)) {
    throw 'DeepOri Bridge local-preview attestation failed; stop setup.'
}
$sortedFiles = [string[]]$expectedFiles.Clone()
[Array]::Sort($sortedFiles, [StringComparer]::Ordinal)
$payload = [Text.StringBuilder]::new()
foreach ($relative in $sortedFiles) {
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($path) -or
        ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Bridge payload path validation failed; stop setup.'
    }
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $null = $payload.Append($relative.Replace('\', '/')).Append([char]0).Append($item.Length).Append([char]0).Append($hash).Append("`n")
}
$sha = [Security.Cryptography.SHA256]::Create()
try { $actualPayload = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload.ToString()))).Replace('-', '') }
finally { $sha.Dispose() }
if ($actualPayload -cne $bridgePayload -or
    $actualPayload -cne $bridgeResponse.BridgePayloadManifestSha256) {
    throw 'Bridge payload manifest mismatch; stop setup.'
}
```

After these checks pass, report only that package/path, payload, preview runtime, and isolated vault attestations passed. Do not print their raw values.

## Accepted-session command pipe

An accepted response includes a one-session `CommandPipeName` and `CommandSessionToken`. Keep both only in the current PowerShell process; neither is a credential and neither belongs in a file, log, or user-facing report. Before every request, the Bridge independently re-attests the live PID, start time, apphost and payload hashes, HWND, and page. A changed or closed page revokes the session.

The only permitted operations are `setup_status`, `remote_menu_status`, `menu_inspect_apply`, `menu_publish`, `feishu_listen`, `owner_gate`, `project_gate`, and `roundtrip_status`. `remote_menu_status` is read-only. `menu_inspect_apply` may inspect the exact remote diff and PATCH only the qualified managed preview menu. `menu_publish` is permitted only after that exact candidate is ready and uses the authorization recorded when the user clicked the automatic-configuration action; it must re-attest the same live Bridge and Bot generation. `feishu_listen` may start the local, identity-generation-verified long connection. `owner_gate`, `project_gate`, and `roundtrip_status` only reveal the corresponding one-at-a-time App Gate; they do not bypass it. Credential reads and every undeclared operation must be rejected. Send a fresh request ID and the exact caller evidence for each operation; never reuse the identity-handshake nonce as authorization by itself.

The initial challenge expires after 120 seconds; an accepted command session has a separate 30-minute maximum and remains bound to the same process and page. If that process exits, or the session expires, do not substitute a new PID or retry writes blindly; obtain a fresh App-issued challenge. Never write session tokens or raw handshake messages to disk.

After the handshake and local-preview checks succeed, define and call this function in that same PowerShell process. It prints only the safe state and message; never print the request, session token, or full response.

```powershell
function Invoke-BridgeSetupCommand {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('setup_status','remote_menu_status','menu_inspect_apply','menu_publish','feishu_listen','owner_gate','project_gate','roundtrip_status')]
        [string]$Operation
    )
    $commandRequest = @{
        Nonce = $bridgeNonce
        SessionToken = $bridgeResponse.CommandSessionToken
        Operation = $Operation
        RequestId = [Guid]::NewGuid().ToString('N')
        ClientProcessId = $bridgeProcess.Id
        ClientProcessStartTimeUtcTicks = $bridgeProcess.StartTime.ToUniversalTime().Ticks
        ClientExecutablePath = $bridgeClientPath
        ClientExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $bridgeClientPath).Hash
        ExpectedBridgePayloadManifestSha256 = $bridgePayload
    } | ConvertTo-Json -Compress
    $commandPipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.', $bridgeResponse.CommandPipeName, [IO.Pipes.PipeDirection]::InOut)
    $commandPipe.Connect(5000)
    $commandWriter = [IO.StreamWriter]::new(
        $commandPipe, [Text.UTF8Encoding]::new($false), 1024, $true)
    $commandWriter.AutoFlush = $true
    $commandReader = [IO.StreamReader]::new(
        $commandPipe, [Text.Encoding]::UTF8, $false, 1024, $true)
    $commandWriter.WriteLine($commandRequest)
    $commandResponse = $commandReader.ReadLine() | ConvertFrom-Json
    $commandPipe.Dispose()
    if (-not $commandResponse.Accepted -or $commandResponse.RequestId -ne ($commandRequest | ConvertFrom-Json).RequestId) {
        throw "Bridge rejected automatic operation: $Operation"
    }
    $commandResponse | Select-Object Accepted,State,Message
}
```

On acceptance, request `setup_status`, then `feishu_listen` before menu operations. Follow visible states one Gate at a time; preserve existing bindings and project authorizations. A rejected or expired handshake permits one newly issued App challenge; an accepted session disables duplicate copy/retry until the user explicitly exits. If required automation is unavailable, explain the limitation and ask the user to exit automatic mode before switching to the manual wizard.

## Workflow

1. Read-only inspect Windows 11 and Codex Desktop, then complete the prompt's named-pipe identity handshake immediately. Use only the exact Bridge PID/HWND/page from that prompt.
2. Continue in the verified Bridge first-connection assistant. Keep App ID and App Secret entry inside that App. Codex may focus or click Bridge controls only after revalidating the exact window; it must never type, read, copy, log, or inspect the Secret field.
3. Ordinary launch and reconnect are read-only and must make zero PATCH or publish calls. In an accepted setup session, `menu_inspect_apply` writes only the exact qualified candidate, without publication. Verify its diff, then send `menu_publish` once. Query `remote_menu_status` every 10 seconds for at most two minutes. If still pending, report the actual review/client-sync state and next action; do not keep publishing or waiting indefinitely. Never ask the user to enter Event Keys manually.
4. If `menu_inspect_apply` returns Feishu code `99991672`, verify the browser is on the prompt's exact App ID and Bot name, add only the missing tenant scope from the allowlist, publish that permission change, and retry automatically. Automatic menu management requires `application:application:self_manage` and `application:bot.menu:write`. Pause only when Feishu requires login, verification, or 2FA; do not turn ordinary permission-page clicks into user instructions.
5. Pause for sign-in/2FA, credential entry, unexpected diff/unknown menu, owner binding, project authorization, real client testing, expired sessions, or unavailable browser/computer-control tools. Give one precise next action and evidence. Review-pending, rejected, published-pending-client-sync, and client-visible-verified are distinct states; never claim pending or untested work succeeded.
6. Bind a user through the App's random six-digit, two-minute one-time code flow. Do not expose the sender ID. A successful binding is acknowledged automatically in Feishu. Save at least one explicit existing project directory; wildcard/all-project access is rejected.
7. After those one-time gates, do not ask the user to arm or confirm each ordinary read-only message. A bound owner may send `Windows Bridge 回执测试` directly, or send `Windows Codex：<question>` to run an automatic read-only Codex task in the explicitly authorized project. Mutating or risky actions still require a human gate.
8. Verify separately that the Bridge is running, the Feishu connection is listening, owner binding is active, project access is explicit, and one user-performed text round trip succeeds.

Required Feishu long-connection consumers are `im.message.receive_v1`, `application.bot.menu_v6`, and `card.action.trigger`. The 12 managed menu Event Keys are `current_task`, `select_task`, `new_task`, `archive_task`, `task_subscriptions`, `sync_desktop`, `sync_desktop_switch`, `task_settings`, `compact_task_context`, `codex_usage`, `promlight`, and `promlight_legend`. The only accepted structure is four groups in this order: `Task 管理` (current_task, select_task, new_task, archive_task), `桌面 Task` (task_subscriptions, sync_desktop, sync_desktop_switch), `模型设置` (task_settings, compact_task_context, codex_usage), and `提示灯` (promlight, promlight_legend). Use the floating display strategy so all four first-level groups remain distinct; never merge `模型设置` and `提示灯`. `Task 管理` children must remain ordered `当前 Task` → `切换 Task` → `新建 Task` → `归档当前 Task`. After the user explicitly starts automatic setup in Bridge, inspect, write the exact candidate, publish it, and verify client visibility without asking for redundant confirmation; pause only when Feishu itself requires sign-in/2FA or rejects publication. Registration or counters are not live callback evidence. Completion requires exact remote structure, the floating display form, correct published state, a real `application.bot.menu_v6` callback, and its corresponding visible business behavior.

The unsigned preview may start its packaged sidecar only from the verified Bridge process, after official bot-info verification and an exact three-consumer ready attestation. This is local preview availability, not a signed production release.

If browser or UI control is unavailable, continue with one precise manual instruction at a time. Do not broaden permissions or install unrelated plugins.

Installed and updated by DeepOri Bridge for Windows.
