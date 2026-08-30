# Release Notes

## 1.11.3 (build 82)

- Clarifies that renaming a PromLight only changes its display name in Bridge and Feishu, without changing the Bluetooth name, stable device ID, connection, or Task associations.

## 1.11.2 (build 81)

- Renames the PromLight Task relationship throughout Feishu from “关注” to the clearer “关联”, keeps add/remove association actions visible together with the unavailable action disabled, and refreshes the Task selector when real Feishu callbacks omit the selector name.

## 1.11.1 (build 80)

- Replaces the full PromLight setup form on the App home page with one compact status and management entry.
- Moves device discovery, compatibility details, device selection, naming, and user assignment into a dedicated second-level PromLight management page.
- Removes the duplicate PromLight form from general Bridge configuration and deduplicates repeated device/product labels.
- Reorganizes each Feishu light into clear “关注哪些 Task” and “设备设置” paths, and explains that removing the Bridge binding stops Task reminders without disconnecting macOS Bluetooth.

## 1.11.0 (build 79)

- Bundles a clean, minimal Universal PromLight Helper inside DeepOri Bridge, so supported Macs no longer need a separate PromLight App installation.
- Discovers each connected PromLight through macOS IOKit, preserves the existing stable local device reference for current bindings, and sends only the four Task aggregate states used by the bridge.
- Uses the public MIT-declared PromLight 5E5E protocol as the interoperability reference without copying the upstream daemon, hooks, HTTP server, macros, or session manager.
- Prefers the built-in Helper and retains PromLight App `0.2.3` only as a migration fallback when the Helper cannot be used.
- Treats a successful HID write as `submitted`, not verified physical output, and verifies that both the App executable and Helper are Universal before installation or update.

## 1.10.2 (build 78)

- Moves the complete local PromLight discovery and authorized-user binding flow onto the App home page while keeping the same reusable controls in Bridge settings.
- Displays the verified PromLight hardware report (`0.1.3`, release `19`), the required PromLight Mac relay version (`0.2.3`), and each connected device's actual reported product/version without exposing its private device reference.
- Keeps unverified hardware or relay versions explicitly outside the compatibility claim and retains the Mac-only continuous-relay boundary.
- Removes local and mobile hardware-pairing actions from Feishu; the card now manages only already-bound lamps, with independent per-lamp Task lists, names, defaults, status, and unbind controls.
- States that PromLight App `0.2.3` is still a separately installed dependency and is not redistributed inside the public Bridge package.

## 1.10.1 (build 77)

- Waits for the Desktop-busy queue worker to finish before its test fixture is removed, eliminating a macOS CI cleanup race without changing production queue behavior.

## 1.10.0 (build 76)

- Adds a persisted, opt-in “自动安装 App 更新” switch while preserving manual update checks; installation waits for all active and queued Task work to finish and keeps the existing signed Universal-package verification.
- Reorganizes App update controls into compact aligned settings, moves logs and the data directory into collapsed advanced maintenance, and keeps GitHub/VPN guidance visible without crowding the main dashboard.
- Restores the standard macOS title-bar boundary and adds a safe top inset so the product logo and title remain fully visible below the window controls.
- Updates current product guidance and in-App help to match the compact Feishu parent menu name “桌面task” without changing any Bot Event Key.
- Makes PromLight card interactions prefer the stable message-card patch path, falling back to callback-token update and durable retry only when needed.
- Immediately replaces the clicked PromLight Task-follow button with a disabled “正在处理…” state before applying the subscription and rendering the final card.
- Renames the compact PromLight legend to “灯光对应的事件说明” so the card clearly explains which Task events drive each light state.

## 1.9.31 (build 69)

- Adds one top-level Feishu menu, “提示灯”, with “我的提示灯” and “灯光状态说明” leaves.
- Adds private per-user PromLight ownership, per-light Task allowlists, defaults, rename/unbind controls, restart recovery, and permission/archive revocation cleanup.
- Aggregates only explicitly followed Tasks as red error > yellow human gate > yellow running > green idle, while refusing to infer uncertain errors.
- Uses the local PromLight device-targeted HTTP relay and distinguishes device ACK from independently verified light output; offline lights keep their last logical status without claiming success.
- Fails closed on unknown Task state, coalesces relay work on one bounded worker, retries no-ACK delivery with backoff, and retains an offline unbind tombstone until the idle command is acknowledged.
- Adds local PromLight discovery and authorized-user assignment to the macOS App. Mobile Feishu mini-app BLE remains fail-closed until the real GATT protocol and device lifecycle pass true-device validation.
- Keeps Ori Hub at its frozen V1 boundary: Hub-compatible device semantics are documented, but user identity, Task authorization, subscriptions, cards, and routing remain in the bridge.

## 1.9.30 (build 68)

- Opens Codex through its registered `codex://` launch URL after copying the setup prompt, so DeepOri Bridge no longer accesses the ChatGPT application bundle and does not trigger macOS App Management protection.

## 1.9.29 (build 67)

- Makes the first-connection choice page more compact, replaces ambiguous repeated actions with explicit Codex and manual setup buttons, and removes the inactive-looking footer label.
- Replaces the focused text-style close control with a standard close icon while preserving its tooltip and accessibility label.

## 1.9.28 (build 64)

- Adds two first-connection paths: Codex-assisted setup with a bundled local Skill and the existing manual four-step assistant.
- Generates a copyable Codex setup prompt, opens Codex Desktop, and keeps App Secret entry out of Codex Tasks, process arguments, files, and logs.
- Rewrites the manual user step around “add a Feishu user” and “choose accessible projects”, with a direct finish path when users are already configured.
- Clarifies that Codex does not need a Feishu plugin; the App continues to bundle its required `lark-cli` runtime.

## 1.9.27 (build 63)

- Keeps GitHub-based App updates in the main management flow and explains when a VPN may be required to reach GitHub.
- Moves local background-service repair into Advanced Maintenance and clarifies that repair uses components bundled in the current App without accessing GitHub.

## 1.9.26 (build 62)

- Introduces the public product name “DeepOri Bridge” while preserving the existing bundle identifier, local data directory, and installed App path for upgrade compatibility.
- Labels this distribution as “for macOS” and shows the macOS 13+ requirement in the product and onboarding surfaces.
- Rebuilds the first-connection assistant around the selected horizontal four-step layout, with one clear work area and a separate status/check panel.
- Keeps automatic checks honest: the App verifies Bot identity and Feishu connectivity, while permissions, events, and version publication remain explicit administrator checks.

## 1.9.25 (build 61)

- Reorganizes the Bot menu under “模型设置”, separates Task model controls from context compaction, and keeps Codex quota as an account-level submenu.
- Adds per-Task Standard/Fast speed selection and allows model, analysis-strength, and speed changes during a running turn for the next unstarted message; context compaction remains idle-only.

## 1.9.24 (build 60)

- Adds a Feishu “Task 运行设置” entry for reading and changing the selected Task's model and reasoning effort, with stale-card and running-Task safeguards.
- Adds explicit confirmation before compacting the selected Task's context and routes model, effort, and compaction changes through Codex Desktop's versioned IPC protocol.
- Shows a compact, deduplicated execution timeline on running cards and preserves it across restart recovery.
- Reads the active Task settings from Codex Desktop's live thread-state snapshot and reduces client-discovery delay before Desktop IPC requests.

## 1.9.23 (build 59)

- Restricts local result links to the selected Task workspace or a dedicated bridge output directory, while keeping current-turn image-generation events available.
- Serializes App and background `state.json` mutations with one process-wide file lock, acknowledges Feishu events after dispatch, and retains seven days of deduplication history.
- Preserves failed final text and attachments instead of silently evicting them at the transient-card queue limit; a full attachment spool rejects the new item and reports that it was not saved.
- Verifies Ogg content before using Feishu native audio, validates first-user discovery with a one-time P2P challenge, and reports restart success only after launchd confirms the service is running.
- Makes the public installer verify GitHub's SHA-256 digest, bundle identity/version, code signature, Universal architectures, and downgrade direction before replacement.

## 1.9.22 (build 58)

- Sends explicitly linked Opus and Ogg Opus results as native playable Feishu audio messages, while returning MP3, WAV, M4A, AAC, and FLAC as audio attachments.
- Recognizes both ordinary Markdown links and Codex Desktop's `![audio](path)` output form before image parsing, so audio is not misclassified as an unavailable image or duplicated as a document.
- Gives audio its own limits, idempotency keys, durable retry type, and cross-restart spool path across normal turns, Desktop handoff, and Task subscriptions.
- Makes the local developer installer rebuild and replace the LaunchAgent runtime before copying the App, preventing a new App version from leaving an older `bridge.py` active.

## 1.9.21 (build 57)

- Keeps general diagnostics independent of the separately installed private automation extension.
- Checks the private workflow client and endpoint only when that extension is explicitly enabled in the local configuration.

## 1.9.20 (build 56)

- Makes restart-recovery tests independent of any developer Mac Codex database so clean GitHub runners verify the same behavior.
- Removes private automation components, policy examples, and documentation from the general public App while preserving separately installed extension files during normal updates.
- Keeps the public bridge operational when no private extension module is installed and retains the 1.9.19 durable turn-recovery behavior.

## 1.9.19 (build 55)

- Persists every accepted Feishu-owned Desktop turn with its original message, progress card, Task identity, and rollout cursor so a bridge restart can continue tracking it.
- Changes an interrupted running card to “恢复中”, removes its stale stop action, and patches the original card when the turn finishes after restart.
- Reuses the original source-message idempotency keys for recovered text, image, and file delivery, preventing duplicate final replies when a restart crosses the normal delivery boundary.

## 1.9.18 (build 54)

- Labels the confirmation after “接续其他 Task” selection as “接续选定的 Task” so it cannot be mistaken for the pre-existing current Task.
- Keeps “接续当前 Task” unchanged for the direct current-Task path and when canceling a Task switch.

## 1.9.17 (build 53)

- Reduces the subscribed-Task completion card to its Task identity, completion state, and one short pointer to the complete result below.
- Removes the duplicated result preview, explanatory paragraph, and subscription-management button while preserving complete text, image, and file delivery.

## 1.9.16 (build 52)

- Renames the Feishu Bot menu parent `TASK` to “Task 管理”.
- Consolidates the former “接续桌面 Task” and “订阅 Task” entries under “管理桌面 Task”, ordered as “订阅桌面 Task”, “接续当前 Task”, and “接续其他 Task”.
- Renames “额度用量” to “Codex 额度用量” while preserving all eight existing Event Keys and callback compatibility.

## 1.9.15 (build 51)

- Delivers the complete text result of every newly completed subscribed Task below its result card instead of limiting the user to the card preview.
- Splits long subscription results into ordered Feishu replies with distinct idempotency keys, and durably retries any failed chunk without rerunning Codex.
- Keeps subscription images and files attached to the same result route and tells the user where the complete result will appear.

## 1.9.14 (build 50)

- Checks an existing lark-cli Profile when the first-connection assistant opens and reuses a healthy Keychain-backed Bot connection without asking for App ID or App Secret again.
- Adds explicit “关闭向导” actions at the top and bottom of the setup assistant while preserving Escape as the cancel shortcut.
- Registers the standard macOS Edit menu so Undo, Cut, Copy, Paste, and Select All work in App ID, App Secret, Profile, and authorization fields.

## 1.9.13 (build 49)

- Refuses App and background-component updates while any Feishu-owned Task is still running, in addition to the existing queue and durable-delivery checks.
- Enforces the same active-run gate inside the installer so command-line or automated updates cannot bypass the App safety check.

## 1.9.12 (build 48)

- Adds the top-level Feishu “订阅 Task” menu so each authorized user can independently subscribe to up to twenty Tasks across permitted projects.
- Persists per-user rollout cursors across restarts, skips historical results, retries failed sends with the same idempotency identity, and removes subscriptions when user, project, or Task access disappears.
- Delivers new Desktop results, images, and linked files to every subscriber while suppressing only the originating user's duplicate bridge-owned reply.
- Adds the eighth Event Key to first connection, advanced configuration, migration, documentation, and release validation.

## 1.9.11 (build 47, unreleased)

- Rebuilds first connection as a focused four-step wizard that shows one task at a time, keeps progress and back navigation visible, and advances after a successful connection check.
- Keeps App ID and App Secret in the primary path while moving the local connection name and raw permission, callback, and Event Key values into Advanced Settings and a complete configuration checklist.
- Replaces deployment-specific onboarding copy with a generic local-processing promise and keeps customer, project, user, Chat, private-server, and private-workflow details out of the public setup UI.

## 1.9.10 (build 46)

- Renames the Desktop handoff submenu from “切换 Task 接续” to the clearer “接续其他 Task” while keeping the compatible `sync_desktop_switch` Event Key unchanged.
- Keeps the distributable example configuration at zero project access until the Mac owner explicitly authorizes projects, and includes all seven Bot menu Event Keys.

## 1.9.9 (build 45)

- Splits the top-level “接续桌面 Task” menu into “接续当前 Task” and “切换 Task 接续” submenus while preserving the existing `sync_desktop` Event Key.
- Adds `sync_desktop_switch` to open the Task selector directly in Desktop handoff mode; choosing a Task proceeds to the existing handoff confirmation card.
- Keeps the current Task path deterministic: no valid current Task enters the same explicit selection flow instead of guessing from Desktop recency.

## 1.9.8 (build 44)

- Renames the user-facing “选择 Task” entry and card language to “切换 Task” while keeping the compatible `select_task` Event Key unchanged.
- Adds “取消切换” whenever a current Task exists; canceling preserves the selected Task and message route, restores its Project filter, and makes the result explicit on the same card.
- In the “接续桌面 Task” flow, canceling only the Task switch returns to the original current-Task confirmation instead of canceling the entire Desktop handoff.

## 1.9.7 (build 43)

- Rejects a Task tap from a Project list that has already been replaced, keeps the current Task unchanged, and refreshes the same card to the latest Project with a clear retry prompt.
- Coalesces repeated taps on the same non-destructive control so an older queued Project, page, refresh, or usage intent cannot waste another card update after a newer tap arrives.
- Updates the primary selection card first, then coalesces run, approval, queue, and current-status identity refreshes in an application-lifecycle background worker instead of blocking the user's event lane.
- Moves Codex archive and restore calls outside the global state lock, shows an immediate processing state, restores an actionable card on failure, and records privacy-safe per-action, Feishu API, Desktop, queue, and background-refresh latency.

## 1.9.6 (build 42)

- Reads only the newest complete Codex turn from the end of large rollout files instead of rescanning the entire Task history whenever “接续桌面 Task” opens.
- Reduces the measured snapshot read for a real 162.8 MB Task from about 3.9 seconds to under one millisecond and keeps incomplete trailing JSONL records retryable.
- Caps a new Feishu menu-card send at five seconds, records its latency, and durably retries a timed-out send with the same idempotency key and card context.

## 1.9.5 (build 41)

- Immediately acknowledges every Feishu card action with a visible “正在处理…” toast while preserving the synchronous callback response required to avoid `108002`.
- Serializes events per authorized user while allowing different users to proceed independently, and moves maintenance/retry work off the ingress loop.
- Limits the first card-update attempt to three seconds, persists failures immediately, retries card patches after 2/5/15-second backoff, and keeps Feishu network failures from blocking later callbacks.
- Records privacy-safe queue, total-event, and Feishu card-update latency without user, Chat, project, Task, event, or message identifiers.
- Updates the original selection card directly when the callback-token update fails, avoiding a separate fallback message and an unnecessary menu reopen.

## 1.9.4 (build 40)

- Automatically activates the exact target Task through Codex Desktop's supported deep link after the first definite `no-client-found`, then retries through the existing Desktop IPC path.
- Restores the previously frontmost macOS app after activation so a Feishu submission does not leave Codex Desktop in front.
- Activates at most once per accepted message, never retries an uncertain submission, and keeps the manual Desktop/CLI/cancel card when activation itself fails.

## 1.9.3 (build 39)

- Immediately disables the “重试 Desktop” action and shows “正在重试 Desktop…” after the callback is accepted, preventing ambiguous repeated taps.
- Changes the card to the normal running state only after Codex Desktop accepts the Task; a failed retry restores the explicit Desktop/CLI/cancel choices.
- Retains backend event idempotency and same-Task run exclusion as independent duplicate-submission protection.

## 1.9.2 (build 38)

- Keeps the “接续桌面 Task” intent while the user filters projects and selects another Task.
- Returns the same card to a confirmation state after selection, showing the newly selected project, Task, and Desktop status before any subscription is created.
- Keeps the handoff-selection context isolated per authorized Feishu user and preserves ordinary Task-menu selection behavior.

## 1.9.1 (build 37, unreleased)

- Renames the top-level Feishu menu to “接续桌面 Task” so its Codex Desktop Task handoff purpose is visible at a glance.
- Uses only the requesting user's bridge-selected current Task; it never guesses another running or recently used Task.
- Shows the exact project, Task title, and latest Desktop state before handoff, with explicit “接续这个 Task”, “选择其他 Task”, and “取消” actions.
- Revalidates the user's current Task when confirmation is clicked so a stale card cannot subscribe to the wrong Task; Roger, Miller, and future allowlisted users remain independently routed.

## 1.9.0 (build 36, unreleased)

- Adds the top-level Feishu “接续桌面” menu (`sync_desktop`) for one-tap handoff from Codex Desktop to mobile Feishu.
- Immediately returns the latest completed Desktop result, or persists a per-user subscription when the selected Desktop Task is still running and pushes its result on completion.
- Chooses an authorized running Task first and otherwise the most recently used Desktop Task, clearly labels the chosen project and Task, follows it as the user's current Task, and preserves subscriptions across bridge restarts.
- Returns result text, generated or linked images, and explicitly linked local files through the existing durable Feishu delivery path without duplicating a bridge-owned run's normal final reply.

- Expires pending CLI-fallback choices after 24 hours both in the background and when an old card is clicked; a newly accepted turn also invalidates older choices for the same user and Task.
- Retries transient Codex Desktop `no-client-found`/pre-submit connection failures and read-only Task-database `OperationalError` failures with short bounded delays, while never replaying an uncertain turn submission.
- Adds privacy-safe Bot menu audit records containing only the configured leaf Event Key and card result, without user, Chat, Task, event, or message identifiers.
- Atomically fills all six Bot menu Event Keys into older local configs without changing existing custom keys, users, project access, Chat bindings, or workflow settings.
- Keeps the bundled patched `lark-cli` doctor output but hides only the false same-base update warning; a genuine newer upstream base-version warning remains visible.
- Isolates installer regression tests from the real per-user LaunchAgent so a test package cannot stop or replace the running bridge.

## 1.8.3 (build 34)

- Reorganizes the Feishu Bot menu into a `TASK` main menu with “当前 Task”, “选择 Task”, “新建 Task”, and “归档当前 Task” submenus, plus a separate top-level “额度用量” entry.
- Adds the `current_task` menu event, which opens the persistent current-Task status card directly without entering the Task selector.
- Validates all five Bot menu Event Keys as non-empty and unique in the Mac App configuration.

## 1.8.2 (build 33, unreleased)

- Adds “当日 Task 用量分析” and “当期 Task 用量分析” as second-level actions inside the Feishu “Codex 用量” card without requiring new Bot menu configuration.
- Ranks only Tasks visible to the authorized Feishu user by Codex rollout Token usage for local-day or current main-quota-period boundaries.
- Explains high use from structured metrics such as cached long-context input, model-call frequency, reasoning output, tool calls, and context compaction, then labels each Task as normal, normally active, or unusually high.
- Reads only the relevant time range from local rollouts in a background worker, keeps a five-minute in-memory cache, and states that Task Token comparisons are not exact per-Task billing or quota deductions.
- Supersedes the unreleased local `1.8.0 (build 31)` quota-curve prototype with Task-attributed analysis.
- Makes the current Feishu Task follow the latest terminal result: completed, failed, and stopped Tasks become current immediately before their final text reply, while ordinary progress updates never steal focus.
- Serializes terminal-result selection and text delivery per Feishu user so concurrent Tasks leave the last delivered result as the current Task and relabel other active cards accurately.

## 1.7.0 (build 30)

- Reads live account limits from Codex app-server `account/rateLimits/read` instead of estimating quota from tokens or stale rollout logs.
- Shows remaining percentage, window duration, and reset time for each Codex limit bucket in the Mac control center.
- Adds a dedicated “Codex 用量” Bot menu and compact refreshable usage card for all bridge-allowlisted users, keeping account usage out of Task status cards and showing the same Mac account quota to each authorized user.
- Refreshes usage in the background every minute so Feishu event handling and Task execution remain responsive.
- Reflows the configuration sheet into a fixed header and footer with one scrollable content area, labeled user fields, grouped user cards, and collapsed advanced Event Key settings.
- Adds regression coverage for multi-bucket normalization, remaining-percentage calculation, and allowlisted-user card visibility and refresh.
- Labels workflow human-gate cards with both their dedicated target Task and the user's current chat Task, explicitly preserving the current Task unless the user chooses to switch.
- Adds post-decision “切换到目标 Task” and “保持当前 Task” actions and removes the need for ambiguous follow-up text such as “已点击”.
- Stops silently falling back when Codex Desktop IPC is unavailable, records the precise reason, and presents explicit retry, confirmed CLI fallback, and cancel actions in Feishu.
- Prevents duplicate Desktop user bubbles by subscribing to live state without replaying complete history; approval requests are extracted directly from snapshot and patch broadcasts.

## 1.6.1 (build 24)

- Uploads supported local PDF, Office, text, and code files explicitly linked by the Codex result, with per-file/count limits and stable filenames.
- Durably spools failed result-file replies in a private bounded directory and retries them with the original idempotency key without rerunning Codex.
- Shows the latest question, latest answer, and completion time on each user's current-status card; a new question clears the previous answer until completion.
- Adds per-user Task favorites and an explicit Task display scope for all, recently used, or favorite Tasks.
- Keeps favorites, recent history, and conversation summaries isolated by authorized Feishu user and stored only in the private bridge state.
- Adds regression coverage for file extraction/reply/retry, summary lifecycle, favorites, recent ordering, scope selection, and callback behavior.
- Supersedes `1.6.0 (build 23)`; source builds remain ad-hoc signed unless Developer ID and notarization credentials are supplied.

## 1.6.0 (build 23)

- Corrects Task identity after switching: old run, queue, approval, and result cards no longer claim to be the current Task.
- Runs different Tasks concurrently up to the configurable global limit while keeping each individual Task strictly serial; queue cards explain the exact reason and capacity.
- Adds explicit Task-list refresh actions and a per-user current-status card with project, Task, run state, queue count, and update time.
- Adds a live Mac dashboard for event consumers, active Tasks, queued inputs, pending deliveries, and the most recent Feishu event, plus a 1—8 Task concurrency setting.
- Adds in-App updates from GitHub Releases with SHA-256, bundle identity, signature, and Universal-architecture verification, an Applications-only replacement helper, rollback, and a pending-work safety gate.
- Adds aggregate-only private runtime health state and regression coverage for concurrency, card roles, refresh/status behavior, installer safety, and updater boundaries.
- Supersedes `1.5.10 (build 22)`; this source build is ad-hoc signed unless the release operator supplies Developer ID and notarization credentials.

## 1.5.10 (build 22)

- Gives running, queued, completed, stopped, failed, and approval cards an additional green “当前 Task” marker while preserving their existing status colors.
- Displays the project and Task title on separate labeled lines across progress, queue, approval, selection, and archive cards.
- Marks Task selection, creation, and restoration as an explicit current-Task change without adding another confirmation step.
- Uses the same three-line current-Task identity banner in current-status, progress fallback, queue fallback, and final text replies.
- Adds regression coverage for the shared identity format, card headers and tags, approval request labeling, and Task-switch feedback.
- Supersedes `1.5.9 (build 21)`; do not publish these fixes under a reused version/build pair.

## 1.5.9 (build 21)

- Adds a visible “取消新建” button to the new-Task card and clears any pending title request without creating a Task.
- Persists the type of each Bot menu card by message so project selectors still route correctly when Feishu omits both the control name and original card content.
- Makes “在此项目新建” use the latest server-confirmed project selection, preventing a stale button payload from creating the Task in the previous project while a card patch is delayed.
- Adds regression coverage for cancel semantics, missing selector metadata, and stale-button project selection.
- Supersedes `1.5.8 (build 20)`; do not publish these fixes under a reused version/build pair.

## 1.5.8 (build 20)

- Creates and names a new Codex Task in one app-server session, using the current `thread/start` contract without the removed `projectId` field.
- Keeps the requested project working directory on the first Feishu turn and restores the user-supplied Task title after Codex generates its first automatic title.
- Resolves a newly created empty Task directly from the Codex state database while the Desktop sidebar catalog is still refreshing.
- Adds an explicit “取消，不归档” action that leaves the current Task selected and performs no Codex archive call.
- Lists authorized archived Tasks from the existing Task card and restores them through the official `thread/unarchive` app-server method.
- Adds “撤销归档”“选择其他 Task”“新建 Task” actions to the completed archive card so users do not end in an unselected dead end.
- Adds regression coverage for new-Task naming/directory preservation, active-versus-archived catalog filtering, cancel semantics, restore callbacks, and post-archive actions.
- Supersedes the local-only `1.5.4`—`1.5.7` builds and public `1.5.3 (build 15)`; do not publish these fixes under a reused version/build pair.

## 1.5.3 (build 15)

- Makes the durable WorkflowStore decision the only correctness authority before any auxiliary event deduplication, so a transient state failure or crash cannot consume a Feishu retry without resuming the workflow.
- Patches both the initial decision card and its one-time 24-hour reminder after a choice, with existing durable card-patch retry handling for network failures.
- Spools workflow card callbacks to a private fsynced inbox before the bundled `lark-cli` returns Feishu's ACK, re-syncs the inbox directory on duplicate delivery, then replays and acknowledges them across bridge restarts without creating a second recovery.
- Persists workflow-choice replies and card-patch retries through private atomic state writes with file and directory fsync before reporting them queued.
- Validates existing `config.json` and `workflow-state.json` JSON/schema before the installer creates directories, changes permissions, replaces runtime files, or stops the old service.
- Adds card and text-reply regression tests for state-save failures, side-effect crash windows, same-event restart recovery, dual-card durable patching, and installer fail-closed behavior.
- Supersedes `1.5.2 (build 14)`; do not publish these fixes under a reused version/build pair.

## 1.5.2 (build 14)

- Finalizes the Ori One Mind proactive workflow contract: fixed workflow ID, exact action schema, private workbench URL allowlist, and one-time decision recovery through the dedicated Codex Task.
- Makes workflow outbox state fail closed on corruption, unsafe permissions, symlinks, ownership changes, or schema drift; atomic writes now fsync both the state file and its directory.
- Reconciles uncertain Task recovery only from the matching dedicated-Task user input and never blindly resubmits it.
- Rejects obvious credentials, database URLs, private keys, and Feishu user/Chat identifiers from notification text before it can reach a card or Task prompt.
- Adds the identifier-safe local `workflow-config` tool and the isolated `TEST-ROUNDTRIP` card/reply check; repeated callbacks consume and patch only once.
- Preflights all package resources and existing private files before runtime replacement, with staged runtime rollback on failure.
- Supersedes `1.5.1 (build 13)` and the previously planned `1.4.3`; do not publish these runtime changes under an older or reused version/build pair.
