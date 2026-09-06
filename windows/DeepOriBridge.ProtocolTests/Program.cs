using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

if (args.Length == 5 && args[0] == "--promlight-held-lease-child")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var registry = new PromLightRegistry(args[1], data => data, data => data);
    using var lease = await registry.AcquireWriteLeaseAsync(args[2], deadline.Token);
    await File.WriteAllTextAsync(args[3], "ready", deadline.Token);
    while (!File.Exists(args[4])) await Task.Delay(20, deadline.Token);
    return;
}

if (args.Contains("--fake-sidecar-child", StringComparer.Ordinal))
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    return;
}

if (args.Contains("--preview-vault-state", StringComparer.Ordinal))
{
    var previewVault = new FeishuCredentialService(
        FeishuCredentialService.DevelopmentIsolatedResourcePrefix);
    FeishuStoredCredential state = previewVault.Load();
    Console.WriteLine(state.HasSecret ? "PREVIEW_VAULT_NONEMPTY" : "PREVIEW_VAULT_EMPTY");
    return;
}

string WorkingDirectory = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(
        WorkingDirectory,
        "windows",
        "DeepOriBridge.ProtocolTests",
        "DeepOriBridge.ProtocolTests.csproj")))
{
    throw new InvalidOperationException(
        "Run protocol tests from the repository root; refusing to inspect another checkout.");
}
int passed = 0;
if (args.Length == 2 && args[0] == "--attachment-file-probe")
{
    // Read-only validation of an explicitly supplied image. Never rename the
    // user's original file through the staging-download branch.
    if (Path.GetExtension(args[1]).Equals(".download", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Provide an image file, not a staging download.");
    CodexInputAttachment attachment = FeishuAttachments.ValidateDownloaded(args[1], new("image", "", ""));
    using JsonDocument input = JsonDocument.Parse(JsonSerializer.Serialize(FeishuAttachments.BuildTurnInput("请检查截图。", [attachment])));
    Assert(input.RootElement[1].GetProperty("type").GetString() == "localImage"
        && input.RootElement[1].GetProperty("path").GetString() == Path.GetFullPath(args[1]),
        "the real image is validated and becomes a localImage input without modifying the original");
    Console.WriteLine($"PASS: real image validation and Codex input serialization ({new FileInfo(args[1]).Length} bytes); no Codex turn started");
    return;
}
await TaskInputQueueRegressionTests.RunAsync(Assert);
await PromLightFleetRegressionTests.RunAsync(Assert);
if (args.Contains("--task-queue-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} durable Task queue and submission assertions");
    return;
}
await AttachmentRegressionTests.RunAsync(Assert);
await ResultResourceRegressionTests.RunAsync(Assert);
if (args.Contains("--attachment-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} attachment assertions");
    return;
}
await TaskFeedbackRegressionTests.RunAsync(Assert);
if (args.Contains("--task-feedback-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} Task feedback assertions");
    return;
}
await DesktopSyncRegressionTests.RunAsync(Assert);
if (args.Contains("--desktop-sync-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} Desktop sync assertions");
    return;
}

if (args.Contains("--desktop-ipc-probe", StringComparer.Ordinal))
{
    await DesktopRoutingRegressionTests.ProbeAsync();
    return;
}
await DesktopRoutingRegressionTests.RunAsync(Assert);
await ProjectListRegressionTests.RunAsync(Assert);
if (args.Contains("--project-list-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} attachment, feedback, routing and project-list assertions");
    return;
}
if (args.Contains("--desktop-routing-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} Desktop routing and PromLight regression assertions");
    return;
}

using (JsonDocument emptyCurrentCard = JsonDocument.Parse(FeishuTaskSelectorCardBuilder.Build(
    [new FeishuProjectCardOption("Project", "project-capability", true)],
    [new FeishuTaskCardOption("Real Task", "Project", "task-capability", false)],
    "refresh-capability", false, choosingCurrent: true)))
{
    JsonElement card = emptyCurrentCard.RootElement;
    Assert(card.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "当前 Task · 请选择",
        "unselected current menu must explain the actionable selection state");
    JsonElement taskSelector = card.GetProperty("body").GetProperty("elements").EnumerateArray()
        .Single(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "task_selector");
    Assert(taskSelector.GetProperty("options").GetArrayLength() == 1
        && taskSelector.GetProperty("initial_option").ValueKind == JsonValueKind.Null,
        "current menu must directly list real Tasks without choosing a default");
}
string currentMenuRuntimeSource = File.ReadAllText(Path.Combine(WorkingDirectory,
    "windows", "DeepOriBridge", "Services", "FeishuEventRuntime.cs"));
string currentMenuBranch = currentMenuRuntimeSource.Split("case \"current_task\":", 2)[1].Split("case \"select_task\":", 2)[0];
Assert(currentMenuBranch.Contains("choosingCurrent: true")
    && !currentMenuBranch.Contains("尚未选择当前 Task。请点击下方按钮"),
    "the production current_task branch must route missing selection directly to the selector");

// Mac persists state.selected per user; Windows must survive a fresh runtime
// without choosing the first/recent Task or reusing another identity's route.
var selectionValues = new Dictionary<string, string>();
string? selectionScope = "isolated-bot-owner-project-A";
var selectionStore = new CurrentTaskSelectionStore(() => selectionScope,
    key => selectionValues.GetValueOrDefault(key), (key, value) => selectionValues[key] = value);
Assert(selectionStore.Load(selectionScope) == CurrentTaskSelection.Empty, "first run must not invent a current Task");
var explicitSelection = new CurrentTaskSelection("real-thread", @"D:\Authorized\Bridge", "desktop-project");
selectionStore.Save(selectionScope, explicitSelection);
await using (var restoredRuntime = new BridgeRuntime(selectionStore))
{
    Assert(restoredRuntime.SelectedThreadId == explicitSelection.ThreadId
        && restoredRuntime.SelectedDesktopProjectId == explicitSelection.DesktopProjectId,
        "a fresh runtime restores the exact explicit Task and project");
    selectionScope = "different-bot-owner-generation";
    Assert(restoredRuntime.SelectedThreadId == "", "identity changes must discard the in-memory route");
    selectionScope = null;
    Assert(restoredRuntime.SelectedThreadId == "", "revoked authorization cannot restore a route");
}
selectionScope = "isolated-bot-owner-project-A";
var restrictedSelectionStore = new CurrentTaskSelectionStore(() => selectionScope,
    key => selectionValues.GetValueOrDefault(key), (key, value) => selectionValues[key] = value,
    root => AuthorizedProjectScope.Contains(@"D:\Authorized\Bridge", root));
selectionStore.Save(selectionScope, explicitSelection with { ProjectRoot = @"C:\" });
Assert(restrictedSelectionStore.Load(selectionScope) == CurrentTaskSelection.Empty,
    "tampered persisted roots cannot expand the live project authorization");
bool unauthorizedSaveRejected = false;
try { restrictedSelectionStore.Save(selectionScope, explicitSelection with { ProjectRoot = @"C:\" }); }
catch (InvalidOperationException) { unauthorizedSaveRejected = true; }
Assert(unauthorizedSaveRejected, "persisting an unauthorized routing directory is rejected");
selectionStore.Save(selectionScope, CurrentTaskSelection.Empty);
Assert(selectionStore.Load(selectionScope) == CurrentTaskSelection.Empty, "cleared selections stay cleared after restart");
selectionValues[selectionScope] = "{broken-json";
Assert(selectionStore.Load(selectionScope) == CurrentTaskSelection.Empty, "corrupt state cannot invent a current Task");
if (args.Contains("--current-selection-tests", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {passed} current Task persistence assertions");
    return;
}

if (args.Contains("--desktop-project-integration", StringComparer.Ordinal))
{
    int index = Array.IndexOf(args, "--desktop-project-integration");
    string authorizedRoot = args[index + 1];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var savedSelections = new Dictionary<string, string>();
    var liveSelectionStore = new CurrentTaskSelectionStore(() => "integration-only",
        key => savedSelections.GetValueOrDefault(key), (key, value) => savedSelections[key] = value);
    await using var runtime = new BridgeRuntime(liveSelectionStore);
    await runtime.StartAsync(timeout.Token);
    IReadOnlyList<CodexDesktopProject> projects = await runtime.GetDesktopProjectsAsync(authorizedRoot, timeout.Token);
    Assert(projects.Count >= 2, "both distinct local sidebar projects must be listed");
    var membership = new Dictionary<string, IReadOnlyList<CodexThreadSummary>>();
    foreach (CodexDesktopProject project in projects)
    {
        IReadOnlyList<CodexThreadSummary> threads = await runtime.GetDesktopProjectThreadsAsync(project.Id, authorizedRoot, timeout.Token);
        Assert(threads.All(thread => thread.EffectiveProjectId == project.AppServerId
            && (thread.ProjectId == project.AppServerId || project.LegacyAssignmentsPending)),
            "every Task must have canonical membership or verified pre-migration Desktop membership");
        Assert(threads.All(thread => thread.AuthorizedForRouting
            == AuthorizedProjectScope.Contains(authorizedRoot, thread.WorkingDirectory)),
            "a multi-root project must label out-of-scope Tasks without authorizing them");
        membership.Add(project.Id, threads);
        CodexThreadSummary? available = threads.FirstOrDefault(thread => thread.AuthorizedForRouting);
        if (available is not null)
        {
            CodexThreadSummary selected = await runtime.SelectDesktopProjectThreadAsync(
                project.Id, available.ThreadId, authorizedRoot, timeout.Token);
            CodexThreadSummary? current = await runtime.GetCurrentThreadAsync(timeout.Token);
            Assert(current?.ThreadId == selected.ThreadId && runtime.SelectedDesktopProjectId == project.Id,
                "selection and current Task must retain the actual project id");
        }
        Console.WriteLine($"DESKTOP PROJECT: {project.Name}; tasks={threads.Count}; available={threads.Count(thread => thread.AuthorizedForRouting)}; legacy={threads.Count(t => t.DesktopAssignedProjectId.Length > 0)}");
        foreach (CodexThreadSummary thread in threads)
            Console.WriteLine($"  TASK: {thread.ThreadId}; title={thread.Title}; project={thread.ProjectId}");
    }
    Assert(membership.Values.SelectMany(threads => threads.Select(thread => thread.ThreadId)).Distinct().Count()
        == membership.Values.Sum(threads => threads.Count), "same-root projects must not share Task lists");
    var nonempty = projects.Where(project => membership[project.Id].Count > 0).ToArray();
    Assert(nonempty.Length >= 2, "both local projects must expose their own authorized Tasks");
    string before = runtime.SelectedThreadId;
    await using (var restarted = new BridgeRuntime(liveSelectionStore))
    {
        await restarted.StartAsync(timeout.Token);
        CodexThreadSummary? restored = await restarted.GetCurrentThreadAsync(timeout.Token);
        Assert(restored?.ThreadId == before && restarted.SelectedDesktopProjectId == runtime.SelectedDesktopProjectId,
            "restarted runtime must revalidate and restore the same real current Task");
    }
    bool blocked = false;
    try
    {
        await runtime.SelectDesktopProjectThreadAsync(nonempty[0].Id,
            membership[nonempty[1].Id][0].ThreadId, authorizedRoot, timeout.Token);
    }
    catch (InvalidOperationException) { blocked = true; }
    Assert(blocked && before == runtime.SelectedThreadId, "cross-project Task selection must fail without changing current Task");
    foreach (CodexDesktopProject project in projects)
    {
        foreach (CodexThreadSummary unavailable in membership[project.Id].Where(thread => !thread.AuthorizedForRouting))
        {
            bool denied = false;
            try { await runtime.SelectDesktopProjectThreadAsync(project.Id, unavailable.ThreadId, authorizedRoot, timeout.Token); }
            catch (InvalidOperationException) { denied = true; }
            Assert(denied && before == runtime.SelectedThreadId, "out-of-scope selection must preserve current Task");
        }
    }
    string card = FeishuTaskSelectorCardBuilder.Build(
        projects.Select((project, i) => new FeishuProjectCardOption(project.Name, $"project-token-{i}", i == 0)).ToArray(),
        [new FeishuTaskCardOption("unavailable task", "project", "", false, false, @"C:\")], "refresh-token", false);
    using JsonDocument cardDocument = JsonDocument.Parse(card);
    JsonElement selector = cardDocument.RootElement.GetProperty("body").GetProperty("elements").EnumerateArray()
        .Single(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "project_selector");
    Assert(selector.GetProperty("options").GetArrayLength() == projects.Count, "card must render all authorized project options");
    Assert(cardDocument.RootElement.GetProperty("body").GetProperty("elements").EnumerateArray()
        .Any(element => element.TryGetProperty("content", out JsonElement content)
            && content.GetString()!.Contains("unavailable task")), "out-of-scope Tasks must be visible with an explanatory label");
    Console.WriteLine($"DESKTOP PROJECT INTEGRATION PASS: projects={projects.Count}; isolation=passed; currentTask=passed; cardOptions=passed");
    return;
}

JsonElement initializeParameters = JsonSerializer.SerializeToElement(
    CodexAppServerClient.CreateInitializeParameters());
Assert(
    initializeParameters.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean(),
    "initialize must negotiate experimentalApi before project/list is used");
using (JsonDocument dedicatedProject = JsonDocument.Parse("""
    {"id":"dedicated","roots":[{"path":"D:\\\\Authorized\\\\Bridge"}]}
    """))
using (JsonDocument broaderProject = JsonDocument.Parse("""
    {"id":"broader","roots":[{"path":"D:\\\\"},{"path":"D:\\\\Authorized\\\\Bridge"}]}
    """))
{
    Assert(
        CodexAppServerClient.HasExactAuthorizedRootSet(
            dedicatedProject.RootElement,
            @"D:\Authorized\Bridge"),
        "a dedicated single-root project must match its exact authorized directory");
    Assert(
        !CodexAppServerClient.HasExactAuthorizedRootSet(
            broaderProject.RootElement,
            @"D:\Authorized\Bridge"),
        "a broader multi-root project must not expose Tasks outside the authorized directory");
}
var uiProjectTask = new CodexThreadSummary(
    "ui-task",
    "开始",
    "开始",
    @"D:\Authorized\Bridge",
    "gpt-5.6-sol",
    "notLoaded",
    [],
    ProjectId: "",
    ThreadSource: "user");
Assert(
    CodexAppServerClient.IsExactRootUserThread(
        uiProjectTask,
        @"D:\Authorized\Bridge",
        requireMissingProjectProjection: true),
    "an exact-root UI-created Task with a missing app-server project projection is eligible");
Assert(
    !CodexAppServerClient.IsExactRootUserThread(
        uiProjectTask with { ThreadSource = "agent_created_thread", ProjectId = "project" },
        @"D:\Authorized\Bridge",
        requireMissingProjectProjection: true),
    "an API agent-created Task must never enter the UI Task fallback");
Assert(
    !CodexAppServerClient.IsExactRootUserThread(
        uiProjectTask with { Title = "Windows Bridge｜默认 Task", ThreadSource = "" },
        @"D:\Authorized\Bridge",
        requireMissingProjectProjection: true),
    "a legacy default Task without objective user provenance must fail closed");
Assert(
    !CodexAppServerClient.IsExactRootUserThread(
        uiProjectTask with { WorkingDirectory = @"D:\Other\Bridge" },
        @"D:\Authorized\Bridge",
        requireMissingProjectProjection: true),
    "a Task from another root must never enter the exact-root fallback");
if (args.Contains("--initialize-capability-test", StringComparer.Ordinal))
{
    Console.WriteLine("INITIALIZE CAPABILITY PASS: experimentalApi=true");
    return;
}
if (args.Contains("--project-scope-integration", StringComparer.Ordinal))
{
    int optionIndex = Array.IndexOf(args, "--project-scope-integration");
    if (optionIndex < 0 || optionIndex + 1 >= args.Length)
    {
        throw new ArgumentException("--project-scope-integration requires an exact saved project path.");
    }
    string? expectedThreadId = optionIndex + 2 < args.Length
        && !args[optionIndex + 2].StartsWith("--", StringComparison.Ordinal)
            ? args[optionIndex + 2]
            : null;
    await RunProjectScopeIntegrationAsync(args[optionIndex + 1], expectedThreadId);
    return;
}
if (args.Contains("--project-selection-integration", StringComparer.Ordinal))
{
    int optionIndex = Array.IndexOf(args, "--project-selection-integration");
    if (optionIndex < 0 || optionIndex + 2 >= args.Length)
    {
        throw new ArgumentException(
            "--project-selection-integration requires an exact saved project path and Task id.");
    }
    await RunProjectSelectionIntegrationAsync(
        args[optionIndex + 1],
        args[optionIndex + 2]);
    return;
}

Assert(
    AuthorizedProjectScope.Contains(
        @"D:\Authorized\Bridge",
        @"D:\Authorized\Bridge\windows",
        _ => null),
    "an explicitly authorized project must include its own child directories");
Assert(
    AuthorizedProjectScope.Contains(
        @"D:\Authorized\Bridge",
        @"D:\OtherCheckout\Bridge",
        path => path.Contains("Bridge", StringComparison.Ordinal)
            ? "https://github.com/example/bridge.git"
            : null),
    "another local checkout may be listed only when its exact Git origin matches the authorized project");
Assert(
    !AuthorizedProjectScope.Contains(
        @"D:\Authorized\Bridge",
        @"D:\OtherProduct\Mesh",
        path => path.Contains("Authorized", StringComparison.Ordinal)
            ? "https://github.com/example/bridge.git"
            : "https://github.com/example/mesh.git"),
    "a different Git origin must remain outside the authorized Task list");

var validBridgeCandidate = new BridgeCandidateAttestation(
    @"D:\Bridge\DeepOriBridge.exe",
    BridgeProductIdentity.ExpectedProductName,
    BridgeProductIdentity.ExpectedPackageName,
    BridgeProductIdentity.ExpectedPackageFamilyName,
    BridgeProductIdentity.ExpectedPublisher,
    BridgeProductIdentity.ExpectedPublisherId,
    BridgeProductIdentity.ExpectedApplicationUserModelId,
    "None",
    4100,
    638900000000000000,
    new string('A', 64),
    "DeepOriBridge.ConnectionSetup.Page",
    DeveloperPathAttested: true,
    PackagePathAttested: true,
    PayloadManifestSha256: new string('B', 64),
    WindowHandle: 0x1234);
Assert(
    BridgeProductIdentity.IsExpectedCandidate(validBridgeCandidate, allowDevelopmentIdentity: true),
    "exact unsigned development Bridge attestation must pass only with explicit dev mode");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(validBridgeCandidate, allowDevelopmentIdentity: false),
    "unsigned package must fail when explicit dev mode is absent");
Assert(
    BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { SignatureKind = "Store" },
        allowDevelopmentIdentity: false),
    "signed exact package identity may pass without dev mode");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { PackagePathAttested = false, SignatureKind = "Store" },
        allowDevelopmentIdentity: false),
    "signed package metadata without canonical package path attestation must fail closed");
var exactLooseDevCandidate = validBridgeCandidate with
{
    PackageName = string.Empty,
    PackageFamilyName = string.Empty,
    Publisher = string.Empty,
    PublisherId = string.Empty,
    ApplicationUserModelId = string.Empty,
    PackagingMode = "unpackaged-dev",
};
Assert(
    BridgeProductIdentity.IsExpectedCandidate(
        exactLooseDevCandidate,
        allowDevelopmentIdentity: true),
    "unpackaged dev candidate requires an explicitly attested fixed output path");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        exactLooseDevCandidate with { DeveloperPathAttested = false },
        allowDevelopmentIdentity: true),
    "arbitrary loose same-name/ProductName candidate must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { ExecutablePath = @"C:\Mesh\DeepOriAgentMesh.exe" },
        allowDevelopmentIdentity: true),
    "Mesh must never be accepted as Bridge");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { PackageName = BridgeProductIdentity.ForbiddenMeshPackageName },
        allowDevelopmentIdentity: true),
    "Bridge executable under Mesh package identity must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { ProductName = "DeepOri Agent Mesh" },
        allowDevelopmentIdentity: true),
    "forged or mismatched ProductName must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { Publisher = "CN=Wrong", PublisherId = "wrong" },
        allowDevelopmentIdentity: true),
    "wrong Publisher identity must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { PackageFamilyName = "wrong_family" },
        allowDevelopmentIdentity: true),
    "wrong package family must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { ApplicationUserModelId = BridgeProductIdentity.ExpectedPackageFamilyName + "!Wrong" },
        allowDevelopmentIdentity: true),
    "wrong application id must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        validBridgeCandidate with { PageAutomationId = "DeepOriAgentMesh.Home" },
        allowDevelopmentIdentity: true),
    "wrong UIA page contract must fail closed");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate([], allowDevelopmentIdentity: true) is null,
    "zero Bridge candidates must fail closed before any action");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate(
    [
        validBridgeCandidate,
        validBridgeCandidate with { ExecutablePath = @"D:\BridgeB\DeepOriBridge.exe", ProcessId = 4101 },
    ], allowDevelopmentIdentity: true) is null,
    "multiple exact Bridge candidates must fail closed before any action");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate(
    [
        validBridgeCandidate with
        {
            ExecutablePath = @"C:\Mesh\DeepOriAgentMesh.exe",
            ProductName = "DeepOri Agent Mesh",
            PackageName = BridgeProductIdentity.ForbiddenMeshPackageName,
        },
    ], allowDevelopmentIdentity: true) is null,
    "Mesh-only installation must fail closed before any action");

using (Process currentProcessForImagePath = Process.GetCurrentProcess())
{
    string queriedProcessPath = FileAttestation.ProcessImagePath(currentProcessForImagePath);
    Assert(
        File.Exists(queriedProcessPath)
        && FileAttestation.CanonicalPath(queriedProcessPath).Equals(
            FileAttestation.CanonicalPath(currentProcessForImagePath.MainModule!.FileName),
            StringComparison.OrdinalIgnoreCase),
        "native process image lookup must return the exact canonical executable path without a startup MainModule race");
}

var validWindowObservation = new BridgeWindowObservation(
    0x1234,
    4100,
    true,
    BridgeProductIdentity.ExpectedProductName,
    "DeepOriBridge.ConnectionSetup.Page");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [validWindowObservation],
        4100,
        "DeepOriBridge.ConnectionSetup.Page") is not null,
    "one observed process-owned HWND with an exact supported UIA page may bind");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [],
        4100,
        "DeepOriBridge.ConnectionSetup.Page") is null,
    "zero observed Bridge HWND candidates must fail closed");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [validWindowObservation, validWindowObservation with { WindowHandle = 0x5678 }],
        4100,
        "DeepOriBridge.ConnectionSetup.Page") is null,
    "multiple observed Bridge HWND candidates must fail closed");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [validWindowObservation with { WindowHandle = 0, Visible = false }],
        4100,
        "DeepOriBridge.ConnectionSetup.Page") is null,
    "stale or invisible HWND must fail closed");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [validWindowObservation with { PageAutomationId = "DeepOriBridge.Spoof" }],
        4100,
        "DeepOriBridge.Spoof") is null,
    "prefix-only UIA page must not satisfy the exact page whitelist");
Assert(
    BridgeProductIdentity.SelectUniqueWindow(
        [validWindowObservation with { PageAutomationId = "DeepOriAgentMesh.Home" }],
        4100,
        "DeepOriBridge.ConnectionSetup.Page") is null,
    "spoofed non-Bridge UIA page must fail closed");

string payloadProbeRoot = Path.Combine(
    @"D:\Evolution\Caches",
    "DeepOriPayloadProbe-" + Guid.NewGuid().ToString("N"));
if (!Path.GetFullPath(payloadProbeRoot).StartsWith(
        @"D:\Evolution\Caches\DeepOriPayloadProbe-",
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("payload probe path escaped the dedicated D: cache root");
}
Directory.CreateDirectory(payloadProbeRoot);
try
{
    string apphostProbe = Path.Combine(payloadProbeRoot, "DeepOriBridge.exe");
    string managedProbe = Path.Combine(payloadProbeRoot, "DeepOriBridge.dll");
    string skillProbeDirectory = Path.Combine(
        payloadProbeRoot,
        "Assets",
        "CodexSkills",
        "deepori-bridge-setup");
    Directory.CreateDirectory(skillProbeDirectory);
    string skillProbe = Path.Combine(skillProbeDirectory, "SKILL.md");
    string markerProbe = Path.Combine(skillProbeDirectory, ".deepori-bridge-managed");
    File.WriteAllText(apphostProbe, "unchanged-apphost");
    File.WriteAllText(managedProbe, "managed-payload-v1");
    File.WriteAllText(skillProbe, "skill-v1");
    File.WriteAllText(markerProbe, "marker-v1");
    string[] payloadProbeFiles =
    [
        "DeepOriBridge.exe",
        "DeepOriBridge.dll",
        @"Assets\CodexSkills\deepori-bridge-setup\SKILL.md",
        @"Assets\CodexSkills\deepori-bridge-setup\.deepori-bridge-managed",
    ];
    string payloadV1 = FileAttestation.PayloadManifestSha256(
        payloadProbeRoot,
        payloadProbeFiles);
    string apphostV1 = FileAttestation.Sha256(apphostProbe);
    File.WriteAllText(managedProbe, "tampered-managed-payload-v2");
    string payloadV2 = FileAttestation.PayloadManifestSha256(
        payloadProbeRoot,
        payloadProbeFiles);
    Assert(
        FileAttestation.FixedTimeEquals(apphostV1, FileAttestation.Sha256(apphostProbe))
        && !FileAttestation.FixedTimeEquals(payloadV1, payloadV2),
        "tampered managed DLL with unchanged apphost must change the canonical payload manifest digest");
    File.WriteAllText(managedProbe, "managed-payload-v1");
    File.WriteAllText(skillProbe, "skill-v2");
    string payloadWithChangedSkill = FileAttestation.PayloadManifestSha256(
        payloadProbeRoot,
        payloadProbeFiles);
    Assert(
        !FileAttestation.FixedTimeEquals(payloadV1, payloadWithChangedSkill),
        "changing bundled setup SKILL.md must change the canonical payload manifest digest");
    File.WriteAllText(skillProbe, "skill-v1");
    File.WriteAllText(markerProbe, "marker-v2");
    string payloadWithChangedMarker = FileAttestation.PayloadManifestSha256(
        payloadProbeRoot,
        payloadProbeFiles);
    Assert(
        !FileAttestation.FixedTimeEquals(payloadV1, payloadWithChangedMarker),
        "changing bundled setup skill marker must change the canonical payload manifest digest");
}
finally
{
    Directory.Delete(payloadProbeRoot, recursive: true);
}

string botFingerprint = FeishuAppIdentityPolicy.Fingerprint(
    "test-app-bridge",
    "verified-bot-open-id",
    "DeepOri Test Bot");
Assert(
    FeishuAppIdentityPolicy.MayStartSidecar(
        "test-app-bridge", "verified-bot-open-id", "DeepOri Test Bot", true, true, botFingerprint),
    "matching verified bot and tenant fingerprint may start sidecar");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar(
        "test-app-bridge", "other-bot-open-id", "DeepOri Test Bot", true, true, botFingerprint),
    "different official bot identity must fail closed");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar(
        "test-app-bridge", "verified-bot-open-id", "DeepOri Test Bot", true, false, botFingerprint),
    "legacy credential without verified identity must fail closed");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar(
        "test-app-bridge", "verified-bot-open-id", "DeepOri Test Bot", false, true, botFingerprint),
    "verified identity without a secret must fail closed");

string testExecutable = Environment.ProcessPath!;
Assert(
    SetupPathAvailabilityPolicy.Evaluate(false, "", "", false, false)
        .ApplicationCreationGuidanceEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(false, "", "", false, false).ManualEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(false, "", "", false, false).CodexEnabled,
    "CLEAN_FIRST_RUN must expose read-only app creation guidance while both sensitive setup paths remain disabled");
Assert(
    !SetupPathAvailabilityPolicy.Evaluate(false, "bot-a", "bot-a", false, true).ManualEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(false, "bot-a", "bot-a", false, true).CodexEnabled,
    "Bridge window identity alone must not bypass the common Bot credential gate");
Assert(
    SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", false, false).ManualEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", false, false).CodexEnabled,
    "verified Bot identity may enable manual setup while Codex remains identity-gated");
Assert(
    SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", false, true).ManualEnabled
    && SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", false, true).CodexEnabled,
    "both setup paths may enable only after their shared Bot identity prerequisite");
Assert(
    !SetupPathAvailabilityPolicy.Evaluate(true, "bot-b", "bot-a", false, true).ManualEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(true, "bot-b", "bot-a", false, true).CodexEnabled,
    "delayed verification of App ID A must not enable either path after input changes to B");
Assert(
    !SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", true, true).ManualEnabled
    && !SetupPathAvailabilityPolicy.Evaluate(true, "bot-a", "bot-a", true, true).CodexEnabled,
    "in-flight verification must keep both setup paths closed");
var delayedVerificationRelease = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
string delayedCurrentInput = "bot-a";
Task<SetupPathAvailability> delayedVerification = Task.Run(async () =>
{
    await delayedVerificationRelease.Task;
    return SetupPathAvailabilityPolicy.Evaluate(
        storedCredentialVerified: true,
        delayedCurrentInput,
        storedAppId: "bot-a",
        verificationInProgress: false,
        productIdentityBound: true);
});
delayedCurrentInput = "bot-b";
delayedVerificationRelease.SetResult();
SetupPathAvailability delayedAvailability = await delayedVerification;
Assert(
    !delayedAvailability.ManualEnabled && !delayedAvailability.CodexEnabled,
    "delayed verify A completion after a real input edit to B must leave both paths disabled");
using (Process currentProcess = Process.GetCurrentProcess())
{
    string handshakeNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var challenge = new BridgeSetupChallenge(
        "DeepOriBridge.Setup.Tests",
        handshakeNonce,
        DateTimeOffset.UtcNow.Add(BridgeSetupHandshakeService.ChallengeLifetime),
        4100,
        638900000000000000,
        new string('B', 64),
        new string('C', 64),
        0x1234,
        "DeepOriBridge.ConnectionSetup.Page",
        testExecutable,
        Path.GetDirectoryName(testExecutable)!,
        "packaged",
        "unsigned-local-preview",
        PackagePathAttested: true,
        DeveloperPathAttested: true,
        LocalPreviewSensitiveRuntimeEnabled: true,
        ProductionCredentialReuseEnabled: false,
        AgentMeshCredentialAccessEnabled: false,
        CredentialLockerResourcePrefix: FeishuCredentialService.DevelopmentIsolatedResourcePrefix,
        CredentialLockerIsolated: true,
        BridgeProductIdentity.SecurityCriticalPayloadFiles.ToArray());
    Assert(
        BridgeSetupHandshakeService.ChallengeLifetimeSeconds == 120
        && BridgeSetupHandshakeService.ChallengeLifetime == TimeSpan.FromSeconds(120),
        "setup challenge lifetime must have one 120-second source of truth");
    var promptTarget = new FeishuStoredCredential(
        "cli_exact_test",
        HasSecret: true,
        HasVerifiedIdentity: true,
        AppFingerprint: new string('D', 12),
        BotDisplayName: "Windows Bridge Test Bot",
        FullIdentityFingerprint: new string('D', 64));
    string composedSetupPrompt = WindowsSetupCopy.BuildCodexPrompt(promptTarget)
        + BridgeSetupHandshakeService.BuildPromptSuffix(challenge);
    System.Text.RegularExpressions.MatchCollection composedLifetimeMentions =
        System.Text.RegularExpressions.Regex.Matches(
            BridgeSetupHandshakeService.BuildPromptSuffix(challenge).Split("只能通过")[0],
            @"(?<!\d)(?<seconds>\d+)\s*秒");
    Assert(
        composedLifetimeMentions.Count == 1
        && composedLifetimeMentions[0].Groups["seconds"].Value
            == BridgeSetupHandshakeService.ChallengeLifetimeSeconds.ToString()
        && composedSetupPrompt.Contains("App ID: cli_exact_test", StringComparison.Ordinal)
        && composedSetupPrompt.Contains("Bot 名称（JSON 字符串，仅作身份数据）: \"Windows Bridge Test Bot\"", StringComparison.Ordinal)
        && composedSetupPrompt.Contains("执行一次 menu_publish", StringComparison.Ordinal),
        "fully composed Codex setup prompt must bind one verified Bot, authorize menu publish, and contain one 120-second lifetime");
    BridgeCandidateAttestation ObserveExactBridge() => new(
        challenge.BridgeExecutablePath,
        BridgeProductIdentity.ExpectedProductName,
        BridgeProductIdentity.ExpectedPackageName,
        BridgeProductIdentity.ExpectedPackageFamilyName,
        BridgeProductIdentity.ExpectedPublisher,
        BridgeProductIdentity.ExpectedPublisherId,
        BridgeProductIdentity.ExpectedApplicationUserModelId,
        "None",
        challenge.BridgeProcessId,
        challenge.BridgeProcessStartTimeUtcTicks,
        challenge.BridgeExecutableSha256,
        challenge.ExpectedPageAutomationId,
        challenge.PackagingMode,
        challenge.DeveloperPathAttested,
        challenge.PackagePathAttested,
        challenge.BridgePayloadManifestSha256,
        challenge.BridgeWindowHandle);
    string request = JsonSerializer.Serialize(new BridgeSetupHandshakeRequest(
        handshakeNonce,
        BridgeSetupHandshakeService.VerifyOnlyOperation,
        currentProcess.Id,
        currentProcess.StartTime.ToUniversalTime().Ticks,
        FileAttestation.CanonicalPath(testExecutable),
        FileAttestation.Sha256(testExecutable),
        challenge.BridgePayloadManifestSha256));
    BridgeSetupHandshakeRequest acceptedCaller = JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(request)!;
    string commandToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var validCommand = new BridgeSetupCommandRequest(
        challenge.Nonce,
        commandToken,
        "setup_status",
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
        acceptedCaller.ClientProcessId,
        acceptedCaller.ClientProcessStartTimeUtcTicks,
        acceptedCaller.ClientExecutablePath,
        acceptedCaller.ClientExecutableSha256,
        challenge.BridgePayloadManifestSha256);
    BridgeSetupClientAttestation ExactLiveCaller() => new(
        currentProcess.Id,
        currentProcess.StartTime.ToUniversalTime().Ticks,
        FileAttestation.CanonicalPath(testExecutable),
        FileAttestation.Sha256(testExecutable),
        HasReparsePoint: false,
        HasExited: false);
    Assert(
        BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller())
        && BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand with { Operation = "menu_publish" }, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller()),
        "every permitted automatic command, including the explicitly authorized menu publish, must re-attest the caller and live Bridge");
    Assert(
        !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand with { SessionToken = new string('0', 64) }, currentProcess.Id, ObserveExactBridge)
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand with { ClientProcessId = currentProcess.Id + 1 }, currentProcess.Id, ObserveExactBridge)
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand with { ClientProcessStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks + 1 }, currentProcess.Id, ObserveExactBridge)
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand with { Operation = "credential_read" }, currentProcess.Id, ObserveExactBridge)
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, () => ObserveExactBridge() with { PageAutomationId = "DeepOriBridge.Home.Page" }),
        "command pipe must fail closed on token, caller, whitelist, or current page mismatch");
    Assert(
        !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller() with { ProcessStartTimeUtcTicks = ExactLiveCaller().ProcessStartTimeUtcTicks + 1 })
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller() with { ExecutablePath = "C:\\reused-pid.exe" })
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller() with { ExecutableSha256 = new string('0', 64) })
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller() with { HasReparsePoint = true })
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => ExactLiveCaller() with { HasExited = true })
        && !BridgeSetupHandshakeService.IsAuthorizedCommand(
            challenge, acceptedCaller, commandToken, validCommand, currentProcess.Id, ObserveExactBridge,
            _ => null),
        "every command must reject a reused PID, changed start/path/hash, reparse point, exited, or missing live caller");
    var replayGuard = new BridgeSetupCommandReplayGuard();
    Assert(
        replayGuard.TryConsume(validCommand.RequestId)
        && !replayGuard.TryConsume(validCommand.RequestId)
        && !replayGuard.TryConsume("not-a-request-id"),
        "each accepted command request ID must be single-use and malformed IDs must fail closed");
    Assert(
        !BridgeSetupHandshakeService.TryParseCommandRequest("{", out _)
        && !BridgeSetupHandshakeService.TryParseCommandRequest("", out _)
        && !BridgeSetupHandshakeService.TryParseCommandRequest("[]", out _)
        && !BridgeSetupHandshakeService.TryParseCommandRequest(new string('x', BridgeSetupHandshakeService.MaxCommandJsonBytes + 1), out _)
        && BridgeSetupHandshakeService.TryParseCommandRequest(
            JsonSerializer.Serialize(validCommand), out BridgeSetupCommandRequest? parsedCommand)
        && parsedCommand == validCommand,
        "malformed action-pipe JSON must be rejected before dispatch while a valid command remains parseable");
    int consumed = 0;
    Func<bool> consume = () => Interlocked.Exchange(ref consumed, 1) == 0;
    Assert(
        BridgeSetupHandshakeService.ValidateRequest(
            challenge, request, DateTimeOffset.UtcNow, ObserveExactBridge, consume).Accepted,
        "fresh exact setup nonce bound to client PID/start/path/hash must pass readonly handshake");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge, request, DateTimeOffset.UtcNow, ObserveExactBridge, consume).Accepted,
        "replayed setup nonce must fail closed");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) },
            request,
            DateTimeOffset.UtcNow,
            ObserveExactBridge).Accepted,
        "stale setup nonce must fail closed");
    string wrongNonceRequest = JsonSerializer.Serialize(
        JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(request)! with
        {
            Nonce = new string('0', 64),
        });
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge, wrongNonceRequest, DateTimeOffset.UtcNow, ObserveExactBridge).Accepted,
        "wrong setup nonce must fail closed");
    string wrongPayloadRequest = JsonSerializer.Serialize(
        JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(request)! with
        {
            ExpectedBridgePayloadManifestSha256 = new string('F', 64),
        });
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge, wrongPayloadRequest, DateTimeOffset.UtcNow, ObserveExactBridge).Accepted,
        "setup handshake must reject unchanged apphost with a different Bridge payload manifest digest");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge with { LocalPreviewSensitiveRuntimeEnabled = false },
            request,
            DateTimeOffset.UtcNow,
            ObserveExactBridge).Accepted,
        "setup handshake must reject a disabled local-preview runtime attestation");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge with { CredentialLockerIsolated = false },
            request,
            DateTimeOffset.UtcNow,
            ObserveExactBridge).Accepted,
        "setup handshake must reject a non-isolated Credential Locker attestation");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge with { BridgePackageInstallLocation = Path.GetPathRoot(testExecutable)! },
            request,
            DateTimeOffset.UtcNow,
            ObserveExactBridge).Accepted,
        "setup handshake must reject a package root that does not contain the exact executable");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge with { PayloadManifestFiles = ["DeepOriBridge.exe"] },
            request,
            DateTimeOffset.UtcNow,
            ObserveExactBridge).Accepted,
        "setup handshake must reject an incomplete payload manifest file list");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge,
            request,
            DateTimeOffset.UtcNow,
            () => ObserveExactBridge() with
            {
                PageAutomationId = "DeepOriBridge.Home.Page",
            }).Accepted,
        "issued setup challenge must reject a live page that changed before acceptance");
    Assert(
        !BridgeSetupHandshakeService.ValidateRequest(
            challenge,
            request,
            DateTimeOffset.UtcNow,
            () => ObserveExactBridge() with
            {
                WindowHandle = challenge.BridgeWindowHandle + 1,
            }).Accepted,
        "issued setup challenge must reject a live HWND that changed before acceptance");

    var pipeChallenge = challenge with
    {
        PipeName = "DeepOriBridge.Setup.Tests." + Guid.NewGuid().ToString("N"),
        Nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5),
    };
    string pipeRequest = JsonSerializer.Serialize(
        JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(request)! with
        {
            Nonce = pipeChallenge.Nonce,
        });
    using var pipeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Task listener = BridgeSetupHandshakeService.ListenOnceForVerificationAsync(
        pipeChallenge,
        pipeTimeout.Token,
        ObserveExactBridge);
    await using (var client = new NamedPipeClientStream(
        ".",
        pipeChallenge.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        await client.ConnectAsync(pipeTimeout.Token);
        await using var writer = new StreamWriter(
            client,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(
            client,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await writer.WriteLineAsync(pipeRequest.AsMemory(), pipeTimeout.Token);
        string responseJson = (await reader.ReadLineAsync(pipeTimeout.Token))!;
        BridgeSetupHandshakeResponse response = JsonSerializer.Deserialize<BridgeSetupHandshakeResponse>(responseJson)!;
        Assert(
            response.Accepted
            && response.Nonce == pipeChallenge.Nonce
            && response.BridgeExecutableSha256 == pipeChallenge.BridgeExecutableSha256
            && response.BridgeExecutablePath == pipeChallenge.BridgeExecutablePath
            && response.BridgePackageInstallLocation == pipeChallenge.BridgePackageInstallLocation
            && response.BuildChannel == "unsigned-local-preview"
            && response.LocalPreviewSensitiveRuntimeEnabled
            && !response.ProductionCredentialReuseEnabled
            && !response.AgentMeshCredentialAccessEnabled
            && response.CredentialLockerIsolated
            && response.CredentialLockerResourcePrefix
                == FeishuCredentialService.DevelopmentIsolatedResourcePrefix
            && response.PayloadManifestFiles.SequenceEqual(
                BridgeProductIdentity.SecurityCriticalPayloadFiles,
                StringComparer.Ordinal),
            "real current-user named pipe must bind nonce and actual client PID/start/path/hash");
    }
    await listener;
    bool replayPipeUnavailable = false;
    await using (var replayClient = new NamedPipeClientStream(
        ".",
        pipeChallenge.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        try
        {
            using var replayTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await replayClient.ConnectAsync(replayTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            replayPipeUnavailable = true;
        }
        catch (TimeoutException)
        {
            replayPipeUnavailable = true;
        }
    }
    Assert(replayPipeUnavailable, "single-use setup pipe must not accept a replay connection");
}
Assert(
    CodexSetupSkillInstaller.ValidateManagedSkill(Path.Combine(
        WorkingDirectory,
        "windows",
        "DeepOriBridge",
        "Assets",
        "CodexSkills",
        "deepori-bridge-setup")),
    "packaged setup skill marker must match the exact SKILL.md SHA-256");
if (!args.Contains("--skip-installed-skill-check", StringComparer.Ordinal)) Assert(
    CodexSetupSkillInstaller.ValidateManagedSkill(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "skills",
        "deepori-bridge-setup")),
    "installed setup skill marker must match the same managed SKILL.md SHA-256");
else Console.WriteLine("SKIP: installed-user Skill check; packaged Skill integrity is still verified.");
Assert(BridgeSetupHandshakeService.AcceptedSessionLifetime > BridgeSetupHandshakeService.ChallengeLifetime
    && BridgeSetupHandshakeService.AcceptedSessionLifetime == TimeSpan.FromMinutes(30),
    "accepted setup has a bounded lifetime separate from its short initial challenge");
foreach (FeishuMenuConfigurationStage menuStage in Enum.GetValues<FeishuMenuConfigurationStage>())
{
    Assert(!SetupCompletionGatePolicy.CanCompleteAutomatic(false, menuStage),
        "menu publication cannot replace local setup gates");
    Assert(SetupCompletionGatePolicy.CanCompleteAutomatic(true, menuStage)
        == (menuStage == FeishuMenuConfigurationStage.VisibleVerified),
        "automatic setup must not finish before a published menu callback is verified");
}
string packagedSetupSkill = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "Assets",
    "CodexSkills",
    "deepori-bridge-setup",
    "SKILL.md"));
Assert(
    packagedSetupSkill.Contains(
        $"{BridgeSetupHandshakeService.ChallengeLifetimeSeconds}-second",
        StringComparison.Ordinal)
    && !packagedSetupSkill.Contains("30-second", StringComparison.Ordinal)
    && !packagedSetupSkill.Contains("30 秒", StringComparison.Ordinal),
    "packaged setup skill must agree with the single 120-second challenge lifetime source");
string connectionSetupXaml = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "ConnectionSetupPage.xaml"));
Assert(
    connectionSetupXaml.Contains(
        "DeepOriBridge.ConnectionSetup.ApplicationCreationGuidance",
        StringComparison.Ordinal)
    && connectionSetupXaml.Contains(
        "OnOpenApplicationCreationGuidanceClicked",
        StringComparison.Ordinal),
    "ConnectionSetup UI must expose an automatable read-only application creation guidance entry");
Assert(
    connectionSetupXaml.Contains("Codex 自动配置进行中", StringComparison.Ordinal)
    && connectionSetupXaml.Contains("退出自动接管", StringComparison.Ordinal)
    && connectionSetupXaml.Contains("CodexAssistedProgressRing", StringComparison.Ordinal),
    "Codex-assisted setup must visibly identify takeover and expose only an explicit exit path");
Assert(
    connectionSetupXaml.Contains("启动连接并自动配置菜单", StringComparison.Ordinal)
    && connectionSetupXaml.Contains("重试提交菜单版本", StringComparison.Ordinal)
    && connectionSetupXaml.Contains("与 Mac 相同的四组十二项悬浮菜单", StringComparison.Ordinal)
    && !connectionSetupXaml.Contains("我已完成，开始检查", StringComparison.Ordinal),
    "guided setup must expose one explicit action that automatically writes and publishes the exact managed menu");
string connectionSetupSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "ConnectionSetupPage.xaml.cs"));
string configurationSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "BridgeConfigurationPage.xaml.cs"));
string feishuRuntimeSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "Services",
    "FeishuEventRuntime.cs"));
Assert(
    feishuRuntimeSource.Contains("StandardInputEncoding = utf8", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("StandardOutputEncoding = utf8", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("StandardErrorEncoding = utf8", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("throwOnInvalidBytes: true", StringComparison.Ordinal),
    "the Windows Feishu sidecar boundary must use strict UTF-8 for stdin, stdout, and stderr");
foreach (string eventKey in new[]
{
    "current_task",
    "select_task",
    "new_task",
    "archive_task",
    "task_subscriptions",
    "sync_desktop",
    "sync_desktop_switch",
    "task_settings",
    "compact_task_context",
    "codex_usage",
    "promlight",
    "promlight_legend",
})
{
    Assert(
        feishuRuntimeSource.Contains($"case \"{eventKey}\"", StringComparison.Ordinal),
        $"published Feishu menu Event Key {eventKey} must have an explicit Windows card handler");
}
foreach (string action in new[]
{
    "deepori.card.promlight.refresh",
    "deepori.card.promlight.legend",
    "deepori.card.promlight.back",
    "deepori.card.promlight.tasks.open",
    "deepori.card.promlight.tasks.add",
    "deepori.card.promlight.tasks.remove",
    "deepori.card.promlight.settings.open",
    "deepori.card.promlight.settings.clear",
})
{
    Assert(
        feishuRuntimeSource.Contains(action, StringComparison.Ordinal),
        $"PromLight control-center action {action} must remain explicitly handled");
}
Assert(
    !feishuRuntimeSource.Contains("deepori.card.promlight.idle", StringComparison.Ordinal)
    && !feishuRuntimeSource.Contains("deepori.card.promlight.running", StringComparison.Ordinal)
    && !feishuRuntimeSource.Contains("deepori.card.promlight.human-gate", StringComparison.Ordinal)
    && !feishuRuntimeSource.Contains("deepori.card.promlight.error", StringComparison.Ordinal),
    "Mac-aligned PromLight cards must not expose manual remote state buttons");
Assert(
    FeishuCurrentTaskCardBuilder.ProjectStatus("notLoaded", routeExecuting: false, routePending: false) == "空闲"
    && FeishuCurrentTaskCardBuilder.ProjectStatus("running", routeExecuting: false, routePending: false) == "运行中"
    && FeishuCurrentTaskCardBuilder.ProjectStatus("notLoaded", routeExecuting: false, routePending: true) == "等待执行",
    "current Task cards must project Codex transport states into the Mac business statuses");
var currentCardUpdatedAt = new DateTimeOffset(2026, 9, 4, 14, 56, 0, TimeSpan.FromHours(8));
string expectedCurrentCardTime = TimeZoneInfo.ConvertTime(currentCardUpdatedAt, TimeZoneInfo.Local)
    .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
using (JsonDocument currentTaskCard = JsonDocument.Parse(FeishuCurrentTaskCardBuilder.Build(
    "Bridge｜总控与集成",
    "Codex-Feishu-Bridge",
    "空闲",
    activeRuns: 0,
    queuedInputs: 0,
    currentCardUpdatedAt,
    "11111111111111111111111111111111",
    "22222222222222222222222222222222")))
{
    JsonElement card = currentTaskCard.RootElement;
    Assert(
        card.GetProperty("schema").GetString() == "2.0"
        && card.GetProperty("header").GetProperty("template").GetString() == "green"
        && card.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "Task：Bridge｜总控与集成"
        && card.GetProperty("header").GetProperty("subtitle").GetProperty("content").GetString() == "项目：Codex-Feishu-Bridge",
        "current Task header must match the Mac title, project subtitle, and green current-state semantics");
    JsonElement[] currentTaskElements = card
        .GetProperty("body")
        .GetProperty("elements")
        .EnumerateArray()
        .ToArray();
    string currentTaskBody = currentTaskElements[0].GetProperty("content").GetString() ?? string.Empty;
    Assert(
        currentTaskBody.Contains("当前状态", StringComparison.Ordinal)
        && currentTaskBody.Contains("空闲", StringComparison.Ordinal)
        && currentTaskBody.Contains("运行中：0 · 排队：0 · 更新：" + expectedCurrentCardTime, StringComparison.Ordinal)
        && currentTaskElements[1].GetProperty("name").GetString() == "deepori.card.current.refresh"
        && currentTaskElements[2].GetProperty("name").GetString() == "deepori.card.selector.open"
        && !currentTaskBody.Contains("notLoaded", StringComparison.Ordinal),
        "current Task body must match the Mac status summary and keep refresh/switch actions without leaking notLoaded");
}
foreach ((PromLightStatus status, byte channel, byte action) in new[]
{
    (PromLightStatus.Idle, (byte)0x01, (byte)0x01),
    (PromLightStatus.Running, (byte)0x02, (byte)0x01),
    (PromLightStatus.HumanGate, (byte)0x02, (byte)0x02),
    (PromLightStatus.Error, (byte)0x04, (byte)0x02),
})
{
    byte[] frame = PromLightProtocol.CreateFrame(status);
    byte checksum = 0;
    for (int index = 2; index <= 7; index++) checksum ^= frame[index];
    Assert(
        frame.Length == PromLightProtocol.ReportSize
        && frame[0] == 0x5E
        && frame[1] == 0x5E
        && frame[4] == channel
        && frame[5] == action
        && frame[8] == checksum,
        $"PromLight {status} must keep its validated 5E5E HID frame and checksum");
}
using (JsonDocument promLightCard = JsonDocument.Parse(FeishuPromLightCardBuilder.BuildControl(
    new PromLightDeviceSnapshot(
        Found: true,
        Opened: true,
        DeviceCount: 1,
        Product: "PromLight",
        DeviceVersion: "0.1.3",
        ReleaseNumber: PromLightProtocol.SupportedReleaseNumber,
        Error: null,
        CheckedAt: new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.FromHours(8))),
    new PromLightActivitySnapshot(
        PromLightStatus.Idle,
        65,
        new DateTimeOffset(2026, 9, 4, 15, 1, 2, TimeSpan.FromHours(8))),
    new PromLightBindingSnapshot(
        "Roger's task Light",
        true,
        ["task-1", "task-2"],
        new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.FromHours(8))),
    ["Bridge | macOS 主线 · Codex-Feishu-Bridge", "Bridge | Windows 平台 · Codex-Feishu-Bridge"],
    "11111111111111111111111111111111",
    "22222222222222222222222222222222",
    "33333333333333333333333333333333",
    "44444444444444444444444444444444")))
{
    JsonElement card = promLightCard.RootElement;
    JsonElement[] elements = card.GetProperty("body").GetProperty("elements").EnumerateArray().ToArray();
    string detail = elements[1].GetProperty("content").GetString() ?? string.Empty;
    Assert(
        card.GetProperty("schema").GetString() == "2.0"
        && card.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "提示灯控制中心"
        && card.GetProperty("header").GetProperty("subtitle").GetProperty("content").GetString() == "我的提示灯 · 1 盏"
        && card.GetProperty("header").GetProperty("icon").GetProperty("token").GetString() == "lightbulb_colorful",
        "PromLight card header must match the Mac control-center semantics");
    Assert(
        detail.Contains("在线 · 当前中继：本地 Bridge", StringComparison.Ordinal)
        && detail.Contains("绿灯常亮 · 已完成/空闲", StringComparison.Ordinal)
        && detail.Contains("设备已 ACK，灯效未独立回读", StringComparison.Ordinal)
        && detail.Contains("Bridge \\| Windows 平台", StringComparison.Ordinal)
        && elements[2].GetProperty("name").GetString() == "deepori.card.promlight.tasks.open"
        && elements[3].GetProperty("name").GetString() == "deepori.card.promlight.settings.open"
        && elements[4].GetProperty("name").GetString() == "deepori.card.promlight.refresh"
        && elements[5].GetProperty("name").GetString() == "deepori.card.promlight.legend",
        "PromLight control center must show truthful device/task state and all Mac control-center actions");
}
using (JsonDocument taskManager = JsonDocument.Parse(FeishuPromLightCardBuilder.BuildTaskManager(
    "Roger's task Light",
    "Codex-Feishu-Bridge",
    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    [
        new FeishuPromLightTaskOption(
            "Bridge | Windows 平台",
            "Codex-Feishu-Bridge",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            IsSelected: true,
            IsAssociated: false),
    ],
    ["Bridge | macOS 主线 · Codex-Feishu-Bridge"],
    "cccccccccccccccccccccccccccccccc",
    "dddddddddddddddddddddddddddddddd",
    "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
    "ffffffffffffffffffffffffffffffff")))
{
    JsonElement[] elements = taskManager.RootElement
        .GetProperty("body")
        .GetProperty("elements")
        .EnumerateArray()
        .ToArray();
    Assert(
        taskManager.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "关联 Task"
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name)
            && name.GetString() == "promlight_project_selector")
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name)
            && name.GetString() == "promlight_task_selector")
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name)
            && name.GetString() == "deepori.card.promlight.tasks.add")
        && elements.Any(element => element.TryGetProperty("content", out JsonElement content)
            && (content.GetString() ?? string.Empty).Contains("可选 Task：1 个", StringComparison.Ordinal))
        && elements.Any(element => element.TryGetProperty("content", out JsonElement content)
            && (content.GetString() ?? string.Empty).Contains("已关联 1 个 Task", StringComparison.Ordinal)),
        "PromLight task manager must match the Mac explicit-association flow");
}
var policyThread = new CodexThreadSummary(
    "task-1",
    "Task",
    "Preview",
    "D:\\Project",
    "model",
    "active",
    ["waitingOnApproval"]);
Assert(
    PromLightTaskStatusPolicy.Resolve(policyThread, PromLightStatus.Running, waitingOnApproval: false)
        == PromLightStatus.HumanGate
    && PromLightTaskStatusPolicy.Aggregate([
        PromLightStatus.Idle,
        PromLightStatus.Running,
        PromLightStatus.HumanGate,
        PromLightStatus.Error]) == PromLightStatus.Error,
    "PromLight must map approval gates and aggregate multiple tasks using Mac priority");
Assert(
    FeishuPromLightCardBuilder.StatusLabel(PromLightStatus.Running) == "黄灯常亮 · 正在处理中"
    && FeishuPromLightCardBuilder.StatusLabel(PromLightStatus.HumanGate) == "黄灯闪烁 · 需要你处理"
    && FeishuPromLightCardBuilder.LegendText.Contains("红灯闪烁：执行出错，请查看 Task", StringComparison.Ordinal),
    "PromLight labels and legend must match the Mac task-state mapping");
Assert(
    feishuRuntimeSource.Contains("_ = InspectMenuReadOnlyAsync(_routeGates.SessionCancellation)", StringComparison.Ordinal)
    && !feishuRuntimeSource.Contains("_ = ConfigureMenuCandidateAsync(_routeGates.SessionCancellation)", StringComparison.Ordinal)
    && connectionSetupSource.Contains("BeginMenuSetupAsync", StringComparison.Ordinal),
    "ordinary ready/restart must use the protocol-level read-only inspect path; only explicit setup may request apply");
string menuConfigurationSource = File.ReadAllText(Path.Combine(
    WorkingDirectory, "windows", "FeishuEventSidecar", "menu_configuration.go"));
int desiredMenuStart = menuConfigurationSource.IndexOf(
    "func plannedCanonicalMenuNodes()",
    StringComparison.Ordinal);
int menuDiffStart = menuConfigurationSource.IndexOf(
    "func desiredCanonicalMenuNodes()",
    StringComparison.Ordinal);
string desiredMenuSource = desiredMenuStart >= 0 && menuDiffStart > desiredMenuStart
    ? menuConfigurationSource[desiredMenuStart..menuDiffStart]
    : string.Empty;
Assert(
    desiredMenuSource.Contains("Name: \"Task 管理\"", StringComparison.Ordinal)
    && desiredMenuSource.Contains("Name: \"桌面 Task\"", StringComparison.Ordinal)
    && desiredMenuSource.Contains("Name: \"模型设置\"", StringComparison.Ordinal)
    && desiredMenuSource.Contains("Name: \"提示灯\"", StringComparison.Ordinal)
    && menuConfigurationSource.Contains("\"promlight_legend\":     true", StringComparison.Ordinal)
    && !desiredMenuSource.Contains("模型与提示灯", StringComparison.Ordinal),
    "Windows menu plan must publish the complete four-group Mac contract");
Assert(
    feishuRuntimeSource.Contains("ConcurrentDictionary<string, MenuCardAction>", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("deepori_token", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("TryRemove(token, out MenuCardAction? action)", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("当前 Task 已变化或不可见；归档确认已失效", StringComparison.Ordinal)
    && !feishuRuntimeSource.Contains("deepori.execute.", StringComparison.Ordinal),
    "menu cards must use one-use owner-bound capabilities, not the shared execute placeholder");
string bridgeRuntimeSource = File.ReadAllText(Path.Combine(
    WorkingDirectory, "windows", "DeepOriBridge", "Services", "BridgeRuntime.cs")).ReplaceLineEndings("\n");
Assert(
    bridgeRuntimeSource.Contains("SelectProjectThreadAsync", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("GetProjectThreadsAsync(\n            projectRoot", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("selection.ProjectRoot", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("if (selected.Archived)", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("await client.UnarchiveThreadAsync(selected.ThreadId", StringComparison.Ordinal)
    && !bridgeRuntimeSource.Contains("public async Task<CodexThreadSummary?> SelectThreadAsync", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("该 Task 已不在当前可见列表中；未切换", StringComparison.Ordinal)
    && bridgeRuntimeSource.Contains("Selecting a Task only changes the Bridge routing target", StringComparison.Ordinal),
    "explicit Task selection and later routing must use the same exact project-scoped visibility projection");
using (JsonDocument unnamedThread = JsonDocument.Parse("""
    {"id":"thread-id","name":null,"preview":"Mac parity title from first message","cwd":"D:\\\\Project","model":null,"status":{"type":"idle"}}
    """))
{
    Assert(
        CodexAppServerClient.ThreadDisplayTitle(unnamedThread.RootElement)
            == "Mac parity title from first message",
        "thread/list name=null must fall back to the required preview instead of rendering repeated unnamed Tasks");
}
string selectorCard = FeishuTaskSelectorCardBuilder.Build(
    "DeepOri Bridge",
    "0123456789abcdef0123456789abcdef",
    [
        new FeishuTaskCardOption(
            "Mac 基线与功能对齐",
            "DeepOri Bridge",
            "abcdef0123456789abcdef0123456789",
            true),
        new FeishuTaskCardOption(
            "Windows 回归",
            "DeepOri Bridge",
            "123456789abcdef0123456789abcdef0",
            false),
    ],
    "fedcba9876543210fedcba9876543210",
    selectionChanged: false);
using (JsonDocument selectorDocument = JsonDocument.Parse(selectorCard))
{
    JsonElement selectorRoot = selectorDocument.RootElement;
    JsonElement[] selectorElements = selectorRoot
        .GetProperty("body")
        .GetProperty("elements")
        .EnumerateArray()
        .ToArray();
    JsonElement projectSelector = selectorElements.Single(element =>
        element.TryGetProperty("name", out JsonElement name)
        && name.GetString() == "project_selector");
    JsonElement taskSelector = selectorElements.Single(element =>
        element.TryGetProperty("name", out JsonElement name)
        && name.GetString() == "task_selector");
    Assert(
        selectorRoot.GetProperty("schema").GetString() == "2.0"
        && selectorRoot.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            == "Task：Mac 基线与功能对齐"
        && projectSelector.GetProperty("tag").GetString() == "select_static"
        && taskSelector.GetProperty("tag").GetString() == "select_static"
        && taskSelector.GetProperty("options").GetArrayLength() == 2
        && !selectorCard.Contains("thread-id", StringComparison.Ordinal),
        "Windows Task selection card must match the Mac Card 2.0 selector shape without exposing Task references");
}
object CardAction(string name, string label, string token, string type = "default") => new
{
    tag = "button",
    text = new { tag = "plain_text", content = label },
    type,
    name,
    value = new { deepori_token = token },
};
string newTaskCard = FeishuTaskManagementCardBuilder.BuildNewTask(
    "Codex-Feishu-Bridge", "a0", "a1", "a2");
using (JsonDocument document = JsonDocument.Parse(newTaskCard))
{
    JsonElement root = document.RootElement;
    JsonElement[] elements = root.GetProperty("body").GetProperty("elements").EnumerateArray().ToArray();
    Assert(
        root.GetProperty("schema").GetString() == "2.0"
        && root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "新建 Codex Task"
        && root.GetProperty("header").GetProperty("subtitle").GetProperty("content").GetString() == "当前项目：Codex-Feishu-Bridge"
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "new_task_project_selector")
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "deepori.card.new.begin"),
        "new Task card must match the Mac project-selection and create flow");
}
string archiveCard = FeishuMacParityCardBuilder.BuildArchiveTask(
    "Bridge｜总控与集成",
    "Codex-Feishu-Bridge",
    busy: false,
    [
        CardAction("deepori.card.archive.confirm", "归档这个 Task…", "a1", "danger"),
        CardAction("deepori.card.archive.cancel", "取消，不归档", "a2"),
    ]);
using (JsonDocument document = JsonDocument.Parse(archiveCard))
{
    JsonElement root = document.RootElement;
    JsonElement[] elements = root.GetProperty("body").GetProperty("elements").EnumerateArray().ToArray();
    Assert(
        root.GetProperty("header").GetProperty("template").GetString() == "red"
        && root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "Task：Bridge｜总控与集成"
        && elements[1].GetProperty("name").GetString() == "deepori.card.archive.confirm"
        && elements[2].GetProperty("name").GetString() == "deepori.card.archive.cancel",
        "archive card must match the Mac destructive state and action order");
}
string subscriptionsCard = FeishuTaskManagementCardBuilder.BuildSubscriptions(
    "Codex-Feishu-Bridge",
    "b0",
    [new FeishuManagedTaskOption("Bridge｜总控与集成", "Codex-Feishu-Bridge", "b1", true)],
    [],
    "b2",
    "b3",
    "b4");
using (JsonDocument document = JsonDocument.Parse(subscriptionsCard))
{
    JsonElement root = document.RootElement;
    string body = root.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString() ?? string.Empty;
    Assert(
        root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "订阅桌面 Task"
        && root.GetProperty("header").GetProperty("subtitle").GetProperty("content").GetString() == "当前项目已订阅 0 个"
        && body.Contains("人工门", StringComparison.Ordinal)
        && subscriptionsCard.Contains("deepori.card.subscription.add", StringComparison.Ordinal),
        "subscription card must expose the selector and toggle with explicitly project-scoped counts");
}
string desktopSyncCard = FeishuMacParityCardBuilder.BuildDesktopSync(
    "Bridge｜总控与集成",
    "Codex-Feishu-Bridge",
    "idle",
    [CardAction("deepori.card.selector.open", "接续其他 Task", "c1")]);
using (JsonDocument document = JsonDocument.Parse(desktopSyncCard))
{
    JsonElement root = document.RootElement;
    Assert(
        root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "从 Codex Desktop 回到飞书"
        && root.GetProperty("header").GetProperty("template").GetString() == "green"
        && root.GetProperty("body").GetProperty("elements")[1].GetProperty("name").GetString() == "deepori.card.selector.open",
        "desktop sync card must match the Mac task identity and provide only a working switch action");
}
string settingsCard = FeishuTaskManagementCardBuilder.BuildModelSettings(
    new CodexThreadSummary("task-a", "Bridge｜总控与集成", "", "D:\\repo", "gpt-5.6-sol", "idle", [], "high", "default"),
    "Codex-Feishu-Bridge",
    [new FeishuModelOption("gpt-5.6-sol", "d1", true)],
    [new FeishuModelOption("高 · high", "d2", true)],
    [new FeishuModelOption("标准", "d3", true)],
    "d4");
using (JsonDocument document = JsonDocument.Parse(settingsCard))
{
    JsonElement root = document.RootElement;
    JsonElement[] elements = root.GetProperty("body").GetProperty("elements").EnumerateArray().ToArray();
    Assert(
        root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "Task：Bridge｜总控与集成"
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "task_model_selector")
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "task_effort_selector")
        && elements.Any(element => element.TryGetProperty("name", out JsonElement name) && name.GetString() == "task_speed_selector"),
        "task settings card must match the Mac model, effort, and speed controls");
}
string compactCard = FeishuMacParityCardBuilder.BuildCompactTaskContext(
    "Bridge｜总控与集成",
    "Codex-Feishu-Bridge",
    [CardAction("deepori.card.compact.confirm", "确认压缩当前 Task 上下文…", "e1")]);
using (JsonDocument document = JsonDocument.Parse(compactCard))
{
    JsonElement root = document.RootElement;
    string body = root.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString() ?? string.Empty;
    Assert(
        root.GetProperty("header").GetProperty("text_tag_list")[1].GetProperty("text").GetProperty("content").GetString() == "上下文压缩"
        && body.Contains("此操作不能撤销", StringComparison.Ordinal)
        && root.GetProperty("body").GetProperty("elements")[1].GetProperty("name").GetString() == "deepori.card.compact.confirm",
        "context compression card must match the Mac warning and expose one confirmed action");
}
var usageSnapshot = new CodexUsageSnapshot(
    CodexUsageAvailability.Fresh,
    [new CodexUsageBucket(
        "codex",
        "Codex",
        "Plus",
        [new CodexUsageWindow(
            "5 小时",
            25,
            75,
            300,
            new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.FromHours(8)))])],
    new DateTimeOffset(2026, 9, 4, 15, 30, 0, TimeSpan.FromHours(8)));
string usageCard = FeishuMacParityCardBuilder.BuildUsage(
    usageSnapshot,
    [CardAction("deepori.card.usage.refresh", "刷新用量", "f1", "primary")]);
using (JsonDocument document = JsonDocument.Parse(usageCard))
{
    JsonElement root = document.RootElement;
    string body = root.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString() ?? string.Empty;
    Assert(
        root.GetProperty("header").GetProperty("title").GetProperty("content").GetString() == "Codex 用量"
        && root.GetProperty("header").GetProperty("subtitle").GetProperty("content").GetString() == "本机 Codex 账号的实时额度"
        && body.Contains("Plus · 5 小时：剩余 75%", StringComparison.Ordinal)
        && root.GetProperty("body").GetProperty("elements")[1].GetProperty("name").GetString() == "deepori.card.usage.refresh",
        "usage card must match the Mac account-window presentation, including Plus five-hour limits and refresh");
}
Assert(
    feishuRuntimeSource.Contains("confirmTitle: \"确认压缩当前 Task 上下文？\"", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("case \"deepori.card.usage.refresh\"", StringComparison.Ordinal)
    && feishuRuntimeSource.Contains("当前 Task 已变化或不可见；模型设置已失效", StringComparison.Ordinal),
    "remaining card actions must keep operation-specific confirmation, real usage refresh, and stale-task rejection");
Assert(
    connectionSetupSource.Contains(
        "已从当前 Windows 用户 Credential Locker 恢复；并非 App 内置默认值",
        StringComparison.Ordinal)
    && configurationSource.Contains(
        "凭据来自当前 Windows 用户 Credential Locker，并非 App 内置默认值",
        StringComparison.Ordinal)
    && connectionSetupSource.Contains(
        "App ID 与 App Secret 保持空白",
        StringComparison.Ordinal)
    && configurationSource.Contains(
        "App ID 与 App Secret 保持空白",
        StringComparison.Ordinal),
    "credential UI must distinguish current-user vault restoration from embedded defaults and promise blank fields for a clean user");
Assert(
    connectionSetupSource.Contains("SetCodexAssistedMode(true)", StringComparison.Ordinal)
    && connectionSetupSource.Contains("ChoiceView.IsHitTestVisible = !active", StringComparison.Ordinal)
    && connectionSetupSource.Contains("ManualView.IsHitTestVisible = !active", StringComparison.Ordinal)
    && connectionSetupSource.Contains("_setupHandshake.RevokeActiveChallenge()", StringComparison.Ordinal),
    "Codex-assisted setup must lock conflicting paths and revoke its nonce on exit");
string handshakeSource = File.ReadAllText(Path.Combine(
    WorkingDirectory, "windows", "DeepOriBridge", "Services", "BridgeSetupHandshakeService.cs"));
Assert(
    handshakeSource.Contains("HandshakeAccepted", StringComparison.Ordinal)
    && handshakeSource.Contains("ListenCommandsAsync", StringComparison.Ordinal)
    && handshakeSource.Contains("IsAuthorizedCommand", StringComparison.Ordinal)
    && handshakeSource.Contains("actualClientProcessId != caller.ClientProcessId", StringComparison.Ordinal)
    && handshakeSource.Contains("ExpectedBridgePayloadManifestSha256", StringComparison.Ordinal)
    && connectionSetupSource.Contains("OnCodexHandshakeAccepted", StringComparison.Ordinal)
    && connectionSetupSource.Contains("RefreshMenuStatusReadOnlyAsync", StringComparison.Ordinal)
    && connectionSetupSource.Contains("CopyAndOpenCodexButton.IsEnabled = false", StringComparison.Ordinal),
    "accepted identity handshake must advance the UI and every automatic command must re-attest the caller and Bridge payload");
Assert(
    connectionSetupSource.Contains("ConfirmAndPublishMenuAsync", StringComparison.Ordinal)
    && connectionSetupSource.Contains("MenuConfigurationBusy", StringComparison.Ordinal)
    && connectionSetupSource.Contains("PublishedPendingClientSync", StringComparison.Ordinal)
    && connectionSetupSource.Contains("VisibleVerified", StringComparison.Ordinal),
    "setup UI must lock during automatic menu work and distinguish submitted, published, and client-visible states");
string diagnosticsSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "DiagnosticsPage.xaml.cs"));
Assert(
    !diagnosticsSource.Contains("真实飞书 sidecar 已禁用", StringComparison.Ordinal)
    && diagnosticsSource.Contains("精确 3/3 consumer ready", StringComparison.Ordinal),
    "Diagnostics must describe attested local-preview sidecar availability instead of claiming it is disabled");
foreach (string pageSourceName in new[]
{
    "MainPage.xaml.cs",
    "BridgeConfigurationPage.xaml.cs",
    "DiagnosticsPage.xaml.cs",
})
{
    string pageSource = File.ReadAllText(Path.Combine(
        WorkingDirectory,
        "windows",
        "DeepOriBridge",
        pageSourceName));
    Assert(
        pageSource.Contains("await Task.Run(() =>", StringComparison.Ordinal)
        && pageSource.Contains("BindPageContract", StringComparison.Ordinal)
        && pageSource.Contains("PageIdentityCommandGate", StringComparison.Ordinal)
        && pageSource.Contains("AllowsSensitiveActions", StringComparison.Ordinal)
        && pageSource.Contains("MayEnable", StringComparison.Ordinal),
        $"{pageSourceName} must bind self-UIA on a worker and gate every sensitive enable path on page identity");
}
string mainPageXaml = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "MainPage.xaml"));
Assert(
    mainPageXaml.Contains("添加飞书机器人", StringComparison.Ordinal)
    && mainPageXaml.Contains("打开飞书开放平台", StringComparison.Ordinal)
    && mainPageXaml.IndexOf("Text=\"添加飞书机器人\"", StringComparison.Ordinal)
        < mainPageXaml.IndexOf("AutomationProperties.Name=\"进入首次连接向导\"", StringComparison.Ordinal),
    "home must present Feishu bot preparation before the first-connection wizard");
string mainWindowSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "MainWindow.xaml.cs"));
Assert(
    mainWindowSource.Contains("RootFrame.Navigate(typeof(MainPage))", StringComparison.Ordinal)
    && !mainWindowSource.Contains("typeof(ConnectionSetupPage)", StringComparison.Ordinal),
    "first launch must land on the ordered home prerequisites instead of skipping directly into the wizard");
string mainPageSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "MainPage.xaml.cs"));
string credentialServiceSource = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "Services",
    "FeishuCredentialService.cs"));
Assert(
    mainPageSource.Contains("FirstConnectionNavigationGate.Resolve", StringComparison.Ordinal)
    && mainPageSource.Contains("BotCredentialFocusParameter", StringComparison.Ordinal)
    && configurationSource.Contains("FirstConnectionNavigationGate.Resolve", StringComparison.Ordinal)
    && connectionSetupSource.Contains("FirstConnectionNavigationGate.Resolve", StringComparison.Ordinal)
    && connectionSetupSource.Contains("typeof(BridgeConfigurationPage)", StringComparison.Ordinal),
    "home, configuration, direct/deep-link, back, and restored setup entry paths must share the current Bot generation gate");
Assert(
    !connectionSetupSource.Contains("OnIntendedIdentityChangedAsync(SetupAppIdTextBox.Text)", StringComparison.Ordinal)
    && !configurationSource.Contains("OnIntendedIdentityChangedAsync(FeishuAppIdTextBox.Text)", StringComparison.Ordinal)
    && connectionSetupSource.Contains("BotSetupTransaction", StringComparison.Ordinal)
    && configurationSource.Contains("BotSetupTransaction", StringComparison.Ordinal)
    && credentialServiceSource.Contains("out bool credentialWriteSucceeded", StringComparison.Ordinal),
    "credential edits must remain drafts; only a tracked Credential Locker write may commit and invalidate the prior generation");
Assert(
    mainPageSource.Contains("attempt <= 2", StringComparison.Ordinal)
    && mainPageSource.Contains("Task.Delay(TimeSpan.FromSeconds(5))", StringComparison.Ordinal),
    "configured home startup must retry one transient Feishu ready failure without user intervention");
foreach (string controlName in new[]
{
    "StartButton",
    "StopButton",
    "RefreshButton",
    "PromLightDetectButton",
    "PromLightAssociateCurrentButton",
    "PromLightIdleButton",
    "PromLightRunningButton",
    "PromLightHumanGateButton",
    "PromLightErrorButton",
})
{
    Assert(
        ElementStartsDisabled(mainPageXaml, controlName),
        $"MainPage {controlName} must be disabled in XAML before page identity binding starts");
}
string configurationPageXaml = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "BridgeConfigurationPage.xaml"));
foreach (string controlName in new[]
{
    "FeishuConnectButton",
    "FeishuDisconnectButton",
    "FeishuOwnerBindButton",
    "FeishuOwnerClearButton",
    "FeishuAppIdTextBox",
    "FeishuAppSecretPasswordBox",
    "FeishuVerifyButton",
    "FeishuClearButton",
    "ProjectPathTextBox",
    "SaveProjectAuthorizationButton",
    "FeishuCodexRouteCaptureButton",
    "FeishuCodexRouteExecuteButton",
    "FeishuCodexRouteClearButton",
})
{
    Assert(
        ElementStartsDisabled(configurationPageXaml, controlName),
        $"BridgeConfigurationPage {controlName} must be disabled in XAML before page identity binding starts");
}
Assert(
    !configurationPageXaml.Contains("FeishuReplyTestButton", StringComparison.Ordinal)
    && !configurationPageXaml.Contains("FeishuReadOnlyRouteCaptureButton", StringComparison.Ordinal)
    && !configurationPageXaml.Contains("FeishuReadOnlyRouteExecuteButton", StringComparison.Ordinal),
    "automatic reply and read-only routing must not expose per-message prepare or execute buttons");
Assert(
    configurationPageXaml.Contains("无需先在电脑上点击准备", StringComparison.Ordinal)
    && configurationPageXaml.Contains("自动创建只读 Codex Task", StringComparison.Ordinal),
    "configuration UI must explain the automatic owner-only read-only flow");
string diagnosticsPageXaml = File.ReadAllText(Path.Combine(
    WorkingDirectory,
    "windows",
    "DeepOriBridge",
    "DiagnosticsPage.xaml"));
Assert(
    ElementStartsDisabled(diagnosticsPageXaml, "RefreshButton"),
    "DiagnosticsPage refresh must be disabled in XAML before page identity binding starts");

var delayedPageIdentityGate = new PageIdentityCommandGate();
int failedBindingGeneration = delayedPageIdentityGate.BeginBinding();
Assert(
    !delayedPageIdentityGate.PageIdentityBound
    && !delayedPageIdentityGate.PageIdentityFaulted
    && !delayedPageIdentityGate.AllowsSensitiveActions
    && !delayedPageIdentityGate.MayEnable(true),
    "sensitive actions and enable paths must remain closed while page identity binding is delayed");
var delayedBindingRelease = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
Task<bool> staleCompletion = Task.Run(async () =>
{
    await delayedBindingRelease.Task;
    return delayedPageIdentityGate.TryMarkBound(failedBindingGeneration);
});
Assert(
    delayedPageIdentityGate.TryMarkFaulted(failedBindingGeneration)
    && delayedPageIdentityGate.PageIdentityFaulted
    && !delayedPageIdentityGate.MayEnable(true),
    "a page identity binding failure must close every later enable request");
delayedBindingRelease.SetResult();
Assert(
    !await staleCompletion
    && !delayedPageIdentityGate.AllowsSensitiveActions
    && !delayedPageIdentityGate.MayEnable(true),
    "a delayed successful continuation must not override an earlier page identity failure");
int invalidatedGeneration = delayedPageIdentityGate.BeginBinding();
delayedPageIdentityGate.Invalidate();
Assert(
    !delayedPageIdentityGate.TryMarkBound(invalidatedGeneration)
    && delayedPageIdentityGate.PageIdentityFaulted
    && !delayedPageIdentityGate.MayEnable(true),
    "an unloaded page must reject stale binding completion and stale finally re-enable paths");
int successfulBindingGeneration = delayedPageIdentityGate.BeginBinding();
Assert(
    delayedPageIdentityGate.TryMarkBound(successfulBindingGeneration)
    && delayedPageIdentityGate.PageIdentityBound
    && !delayedPageIdentityGate.PageIdentityFaulted
    && delayedPageIdentityGate.MayEnable(true),
    "sensitive enable paths may open only after the current page identity binding succeeds");
bool runCredentialLockerIntegration = !args.Contains(
    // Host broker integration is explicitly selectable, never reported as passing when skipped.
    "--skip-credential-locker-integration",
    StringComparer.Ordinal);
Assert(PromLightDeviceSelectionPolicy.Select(["lamp-a", "lamp-b"], null) is null,
    "ambiguous multi-lamp inventory must never silently select the first device");
Assert(PromLightDeviceSelectionPolicy.Select(["lamp-b", "lamp-a"], "lamp-a") == "lamp-a",
    "reordering devices must preserve the exact lamp target");
Assert(PromLightDeviceSelectionPolicy.Select(["lamp-b"], "lamp-a") is null,
    "disconnecting lamp A must never transfer its tasks to lamp B");
Assert(PromLightDeviceSelectionPolicy.Select(["lamp-a"], null) == "lamp-a"
    && PromLightDeviceSelectionPolicy.Select([], "lamp-a") is null,
    "single-device discovery remains available and an absent target fails closed");
if (!runCredentialLockerIntegration)
{
    Console.WriteLine(
        "SKIP: real Credential Locker integration probes; the host PasswordVault broker and credential persistence have not been verified by this run.");
}
if (runCredentialLockerIntegration)
{
string isolatedVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
var isolatedVault = new FeishuCredentialService(isolatedVaultPrefix);
try
{
    Assert(
        isolatedVault.IsolatedEntryCountForTests() == 0
        && isolatedVault.Load().AppId.Length == 0
        && !isolatedVault.Load().HasSecret,
        "real PasswordVault isolated namespace must begin as CLEAN_FIRST_RUN");
    SetupPathAvailability cleanFirstRunAvailability =
        SetupPathAvailabilityPolicy.Evaluate(false, "", "", false, true);
    Assert(
        cleanFirstRunAvailability.ApplicationCreationGuidanceEnabled
        && !cleanFirstRunAvailability.ManualEnabled
        && !cleanFirstRunAvailability.CodexEnabled
        && !isolatedVault.IsSensitiveRuntimeNamespaceAllowed(
            developmentIdentity: true)
        && !FeishuIdentityGenerationPolicy.MayStart("", isolatedVault.Load()),
        "empty isolated vault may reach app creation guidance while Manual/Codex sensitive paths and sidecar preflight remain disabled");
    var fixedPreviewNamespace = new FeishuCredentialService(
        FeishuCredentialService.DevelopmentIsolatedResourcePrefix);
    Assert(
        fixedPreviewNamespace.IsSensitiveRuntimeNamespaceAllowed(
            developmentIdentity: true),
        "only the exact fixed unsigned-preview namespace may satisfy the development sensitive-runtime namespace gate");
    var cleanUiTestNamespace = new FeishuCredentialService(
        FeishuCredentialService.CleanFirstRunUiTestResourcePrefix
        + "0123456789ABCDEF0123456789ABCDEF");
    Assert(
        !cleanUiTestNamespace.IsSensitiveRuntimeNamespaceAllowed(
            developmentIdentity: true)
        && !cleanUiTestNamespace.IsSensitiveRuntimeNamespaceAllowed(
            developmentIdentity: false),
        "clean-first-run UI automation namespace must hard-disable sensitive runtime in every build identity");
    isolatedVault.SeedLegacyCredentialForTests("test-app", "test-secret-not-persisted");
    FeishuStoredCredential legacyCredential = isolatedVault.Load();
    Assert(
        legacyCredential.HasSecret && !legacyCredential.HasVerifiedIdentity,
        "legacy v2 PasswordVault credential must remain unverified");
    FeishuVerificationResult legacyReuse = await isolatedVault.VerifyAndSaveAsync(
        "test-app",
        string.Empty,
        CancellationToken.None);
    Assert(
        !legacyReuse.Success,
        "legacy PasswordVault secret must not be silently reused for migration");
    isolatedVault.PromoteIdentityForTests(
        "test-app",
        "new-test-secret-not-persisted",
        "verified-bot-open-id",
        "Verified Test Bot");
    FeishuStoredCredential promotedCredential = isolatedVault.Load();
    Assert(
        promotedCredential.HasSecret
        && promotedCredential.HasVerifiedIdentity
        && promotedCredential.BotDisplayName == "Verified Test Bot"
        && promotedCredential.IdentityGeneration.Length == 32,
        "explicit re-verification must migrate to one verified v3 identity generation");
    Assert(
        isolatedVault.IsolatedEntryCountForTests() == 2,
        "successful v3 migration must keep only the identity bundle and its empty fail-closed authorization baseline");
}
finally
{
    isolatedVault.Clear();
}
Assert(
    isolatedVault.IsolatedEntryCountForTests() == 0,
    "isolated PasswordVault migration probe must leave no credential residue");

string stableVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
var stableVault = new FeishuCredentialService(stableVaultPrefix);
try
{
    stableVault.PromoteIdentityForTests(
        "stable-app",
        "stable-secret-not-persisted",
        "stable-bot-id",
        "Stable Bot");
    stableVault.BindOwner("stable-owner-id");
    Assert(
        stableVault.SaveProjectAuthorization(WorkingDirectory).IsAuthorized,
        "verified identity may save one explicit project authorization");
    string? firstOwnerScope = stableVault.CurrentTaskSelectionScope("stable-owner-id");
    stableVault.BindOwner("second-owner-id");
    Assert(stableVault.SaveProjectAuthorization(Path.Combine(WorkingDirectory, "windows")).IsAuthorized,
        "second owner independently authorizes a different project root");
    string? secondOwnerScope = stableVault.CurrentTaskSelectionScope("second-owner-id");
    Assert(firstOwnerScope is not null && secondOwnerScope is not null && firstOwnerScope != secondOwnerScope
        && stableVault.CurrentTaskSelectionScope("stable-owner-id") == firstOwnerScope,
        "adding another owner preserves the first owner's project and subscription scope");
    stableVault.BindOwner("stable-owner-id");
    Assert(stableVault.CurrentTaskSelectionScope("stable-owner-id") == firstOwnerScope
        && stableVault.CurrentTaskSelectionScope("second-owner-id") == secondOwnerScope,
        "switching active owner leaves both sender-specific scopes unchanged");
    var stableVaultAfterUpgrade = new FeishuCredentialService(stableVaultPrefix);
    FeishuStoredCredential restoredAfterUpgrade = stableVaultAfterUpgrade.Load();
    Assert(
        restoredAfterUpgrade.AppId == "stable-app"
        && restoredAfterUpgrade.HasSecret
        && restoredAfterUpgrade.HasVerifiedIdentity
        && stableVaultAfterUpgrade.LoadOwnerBinding().IsBound
        && stableVaultAfterUpgrade.LoadProjectAuthorization().IsAuthorized,
        "an existing Windows user upgrade must restore its verified Credential Locker identity and authorization instead of embedding or clearing defaults");
    stableVault.PromoteIdentityForTests(
        "stable-app",
        "replacement-secret-not-persisted",
        "stable-bot-id",
        "Stable Bot");
    Assert(
        stableVault.LoadOwnerBinding().IsBound
        && stableVault.LoadProjectAuthorization().IsAuthorized,
        "same full verified identity fingerprint may retain authorization across generation refresh");
    stableVault.PromoteIdentityForTests(
        "stable-app",
        "changed-secret-not-persisted",
        "different-bot-id",
        "Different Bot");
    Assert(
        !stableVault.LoadOwnerBinding().IsBound
        && !stableVault.LoadProjectAuthorization().IsAuthorized,
        "same App ID with a changed bot fingerprint must not reuse owner or project authorization");
}
finally
{
    stableVault.Clear();
}

for (int faultStep = 1; faultStep <= 3; faultStep++)
{
    string ownerCommitVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
    var ownerCommitVault = new FeishuCredentialService(ownerCommitVaultPrefix);
    const string interruptedSender = "interrupted-owner-sender";
    try
    {
        ownerCommitVault.PromoteIdentityForTests(
            "owner-commit-app",
            "owner-commit-secret-not-persisted",
            "owner-commit-bot-id",
            "Owner Commit Bot");
        bool bindReportedSuccess = true;
        try
        {
            ownerCommitVault.BindOwnerWithInterruptedCommitForTests(
                interruptedSender,
                faultStep);
        }
        catch (InvalidOperationException)
        {
            bindReportedSuccess = false;
        }

        var restartedOwnerCommitVault = new FeishuCredentialService(ownerCommitVaultPrefix);
        bool activeNow = ownerCommitVault.MatchesBoundOwner(interruptedSender)
            && ownerCommitVault.LoadOwnerBinding().IsBound;
        bool activeAfterRestart = restartedOwnerCommitVault.MatchesBoundOwner(interruptedSender)
            && restartedOwnerCommitVault.LoadOwnerBinding().IsBound;
        Assert(
            faultStep == 3
                ? bindReportedSuccess && activeNow && activeAfterRestart
                : !bindReportedSuccess && !activeNow && !activeAfterRestart,
            $"owner bind fault step {faultStep} must report the same commit result observed before and after service restart");
    }
    finally
    {
        ownerCommitVault.Clear();
    }
}

for (int faultStep = 1; faultStep <= 2; faultStep++)
{
    string ownerRevokeVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
    var ownerRevokeVault = new FeishuCredentialService(ownerRevokeVaultPrefix);
    const string revokedSender = "revoked-owner-sender";
    try
    {
        ownerRevokeVault.PromoteIdentityForTests(
            "owner-revoke-app",
            "owner-revoke-secret-not-persisted",
            "owner-revoke-bot-id",
            "Owner Revoke Bot");
        ownerRevokeVault.BindOwner(revokedSender);
        Assert(
            ownerRevokeVault.MatchesBoundOwner(revokedSender),
            $"owner revoke fault step {faultStep} precondition must bind the sender");
        try
        {
            ownerRevokeVault.ClearOwnerBindingWithInterruptedCommitForTests(faultStep);
            Assert(false, $"owner revoke fault step {faultStep} must interrupt cleanup");
        }
        catch (InvalidOperationException)
        {
        }

        var restartedOwnerRevokeVault = new FeishuCredentialService(ownerRevokeVaultPrefix);
        Assert(
            !ownerRevokeVault.MatchesBoundOwner(revokedSender)
            && !ownerRevokeVault.LoadOwnerBinding().IsBound
            && !restartedOwnerRevokeVault.MatchesBoundOwner(revokedSender)
            && !restartedOwnerRevokeVault.LoadOwnerBinding().IsBound,
            $"owner revoke fault step {faultStep} must deny every legacy owner before and after service restart");
    }
    finally
    {
        ownerRevokeVault.Clear();
    }
}

for (int faultStep = 1; faultStep <= 3; faultStep++)
{
    string crashVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
    var crashVault = new FeishuCredentialService(crashVaultPrefix);
    try
    {
        crashVault.PromoteIdentityForTests(
            "crash-app",
            "old-secret-not-persisted",
            "old-bot-id",
            "Old Bot");
        crashVault.BindOwner("old-owner-id");
        Assert(
            crashVault.SaveProjectAuthorization(WorkingDirectory).IsAuthorized,
            $"fault step {faultStep}: old identity setup must create explicit authorization");
        try
        {
            crashVault.PromoteIdentityWithInterruptedCleanupForTests(
                "crash-app",
                "new-secret-not-persisted",
                "new-bot-id",
                "New Bot",
                faultStep);
            Assert(false, $"fault step {faultStep}: injected vault publication interruption must throw");
        }
        catch (InvalidOperationException)
        {
        }

        var restartedVault = new FeishuCredentialService(crashVaultPrefix);
        Assert(
            !restartedVault.LoadOwnerBinding().IsBound
            && !restartedVault.LoadProjectAuthorization().IsAuthorized,
            $"fault step {faultStep}: restart must never reuse old authorization after interrupted identity publication");
    }
    finally
    {
        crashVault.Clear();
    }
    Assert(
        crashVault.IsolatedEntryCountForTests() == 0,
        $"fault step {faultStep}: crash-consistency test must leave no PasswordVault residue");
}
}

var verifiedIdentityGeneration = new FeishuStoredCredential(
    "test-app",
    true,
    HasVerifiedIdentity: true,
    AppFingerprint: new string('A', 12),
    BotDisplayName: "Verified Test Bot",
    IdentityGeneration: new string('B', 32));
var unverifiedIdentity = new FeishuStoredCredential("", false);
Assert(
    FirstConnectionNavigationGate.Resolve(unverifiedIdentity)
        == FirstConnectionDestination.BotCredentialPreparation
    && FirstConnectionNavigationGate.Resolve(verifiedIdentityGeneration)
        == FirstConnectionDestination.FirstConnectionAssistant,
    "home, deep-link, and restore navigation must route through the same current Bot generation gate");
var cancelledBotSetup = new BotSetupTransaction(verifiedIdentityGeneration);
Assert(
    cancelledBotSetup.VerifiedBeforePresentation
    && cancelledBotSetup.CurrentlyVerified
    && !cancelledBotSetup.CredentialWriteSucceeded
    && cancelledBotSetup.CancelMayRestorePriorPresentation(verifiedIdentityGeneration),
    "editing then cancelling before any Credential Locker write must preserve the prior verified generation");
cancelledBotSetup.ObserveVerification(
    new FeishuVerificationResult(
        false,
        "verification failed",
        CredentialWriteSucceeded: false,
        CurrentlyVerified: true),
    verifiedIdentityGeneration);
Assert(
    cancelledBotSetup.CurrentlyVerified
    && cancelledBotSetup.CancelMayRestorePriorPresentation(verifiedIdentityGeneration),
    "failed verification and pre-write exceptions must leave the prior verified state restorable");
var committedIdentityGeneration = verifiedIdentityGeneration with
{
    AppId = "new-app",
    IdentityGeneration = new string('C', 32),
};
var committedBotSetup = new BotSetupTransaction(verifiedIdentityGeneration);
committedBotSetup.ObserveVerification(
    new FeishuVerificationResult(
        true,
        "verified",
        CredentialWriteSucceeded: true,
        CurrentlyVerified: true),
    committedIdentityGeneration);
Assert(
    committedBotSetup.CredentialWriteSucceeded
    && committedBotSetup.CurrentlyVerified
    && !committedBotSetup.CancelMayRestorePriorPresentation(verifiedIdentityGeneration),
    "after a successful Credential Locker write, cancel must never resurrect the old identity generation");
var interruptedAfterWrite = new BotSetupTransaction(verifiedIdentityGeneration);
interruptedAfterWrite.ObserveVerification(
    new FeishuVerificationResult(
        false,
        "post-write interruption",
        CredentialWriteSucceeded: true,
        CurrentlyVerified: false),
    new FeishuStoredCredential("", false));
Assert(
    interruptedAfterWrite.CredentialWriteSucceeded
    && !interruptedAfterWrite.CurrentlyVerified
    && !interruptedAfterWrite.CancelMayRestorePriorPresentation(verifiedIdentityGeneration),
    "an exception after the credential write boundary must fail closed instead of pretending to restore the old identity");
Assert(
    SetupCompletionGatePolicy.CanComplete(true, true, true, 3, 3, 1, 1),
    "setup may complete only when current identity, owner, project, three consumers, and a real text round trip all pass");
Assert(
    !SetupCompletionGatePolicy.CanComplete(false, true, true, 3, 3, 1, 1)
    && !SetupCompletionGatePolicy.CanComplete(true, false, true, 3, 3, 1, 1)
    && !SetupCompletionGatePolicy.CanComplete(true, true, false, 3, 3, 1, 1)
    && !SetupCompletionGatePolicy.CanComplete(true, true, true, 2, 3, 1, 1)
    && !SetupCompletionGatePolicy.CanComplete(true, true, true, 3, 3, 0, 1)
    && !SetupCompletionGatePolicy.CanComplete(true, true, true, 3, 3, 1, 0),
    "identity, owner, project, consumers, and text round-trip gates must remain independent and non-substitutable");
Assert(
    FeishuIdentityGenerationPolicy.MayStart("test-app", verifiedIdentityGeneration),
    "exact UI intended identity and verified vault generation may enter runtime preflight");
Assert(
    !FeishuIdentityGenerationPolicy.MayStart("other-app", verifiedIdentityGeneration),
    "UI intended app mismatch must fail before runtime start");
Assert(
    FeishuIdentityGenerationPolicy.RequiresAtomicInvalidation(
        FeishuEventRuntimeState.Connecting,
        "other-app",
        verifiedIdentityGeneration),
    "identity switch during Connecting must atomically stop and invalidate authorization generation");
Assert(
    FeishuIdentityGenerationPolicy.RequiresAtomicInvalidation(
        FeishuEventRuntimeState.Configuring,
        "test-app",
        verifiedIdentityGeneration),
    "identity edit during Configuring must cancel the in-flight verification generation");
Assert(
    FeishuIdentityGenerationPolicy.RequiresAtomicInvalidation(
        FeishuEventRuntimeState.Listening,
        "other-app",
        verifiedIdentityGeneration),
    "identity switch during Listening must stop sidecar and clear routes");

var transactionCoordinator = new FeishuIdentityTransactionCoordinator();
long transactionStartVersion = transactionCoordinator.CaptureStartVersion();
await transactionCoordinator.WaitAsync(CancellationToken.None);
CancellationTokenSource startAttempt = transactionCoordinator.BeginStart(
    CancellationToken.None,
    transactionStartVersion);
Task durableMutation = transactionCoordinator.CancelStartAndWaitDurablyAsync();
await Task.Delay(50);
Assert(
    startAttempt.IsCancellationRequested && !durableMutation.IsCompleted,
    "verify-vs-start identity mutation must cancel the active start and serialize behind it");
transactionCoordinator.EndStart(startAttempt);
transactionCoordinator.Release();
await durableMutation.WaitAsync(TimeSpan.FromSeconds(2));
transactionCoordinator.Release();
Assert(
    durableMutation.IsCompletedSuccessfully,
    "identity invalidation must finish after the canceled start releases the shared transaction");

var preflightRaceCoordinator = new FeishuIdentityTransactionCoordinator();
long preflightVersion = preflightRaceCoordinator.CaptureStartVersion();
await preflightRaceCoordinator.WaitAsync(CancellationToken.None);
Task preflightMutation = preflightRaceCoordinator.CancelStartAndWaitDurablyAsync();
await Task.Delay(50);
CancellationTokenSource preflightAttempt = preflightRaceCoordinator.BeginStart(
    CancellationToken.None,
    preflightVersion);
Assert(
    preflightAttempt.IsCancellationRequested,
    "identity mutation between gate entry and start activation must cancel the stale preflight generation");
preflightRaceCoordinator.EndStart(preflightAttempt);
preflightRaceCoordinator.Release();
await preflightMutation.WaitAsync(TimeSpan.FromSeconds(2));
preflightRaceCoordinator.Release();

var delayedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var delayedAttempt = new CancellationTokenSource();
var delayedGates = new FeishuRouteGateCoordinator();
delayedGates.BeginSession(ownerBound: true);
int sidecarsBeforeDelayedReady = Process.GetProcessesByName("feishu-event-sidecar").Length;
Stopwatch delayedReadyStopwatch = Stopwatch.StartNew();
Task delayedReadyWait = FeishuRuntimeStartPolicy.WaitForReadyAsync(
    delayedReady.Task,
    delayedAttempt.Token,
    TimeSpan.FromSeconds(20));
await Task.Delay(TimeSpan.FromMilliseconds(10_250));
delayedAttempt.Cancel();
delayedGates.Invalidate(ownerBound: false, listening: false);
bool delayedReadyCanceled = false;
try
{
    await delayedReadyWait;
}
catch (OperationCanceledException)
{
    delayedReadyCanceled = true;
}
delayedReady.TrySetResult();
delayedReadyStopwatch.Stop();
Assert(
    delayedReadyCanceled
    && delayedReadyStopwatch.Elapsed >= TimeSpan.FromSeconds(10)
    && delayedReadyStopwatch.Elapsed < TimeSpan.FromSeconds(20)
    && !delayedGates.State.Listening
    && !delayedGates.State.ControlledArmed
    && !delayedGates.State.ReadOnlyArmed
    && Process.GetProcessesByName("feishu-event-sidecar").Length == sidecarsBeforeDelayedReady,
    "App ID change during a 10-20s delayed ready must cancel the production wait policy, clear gates, and leave sidecar count unchanged");
delayedGates.Dispose();

if (runCredentialLockerIntegration)
{
string delayedRuntimeVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
var delayedRuntimeVault = new FeishuCredentialService(delayedRuntimeVaultPrefix);
int delayedRuntimeChildPid = 0;
try
{
    delayedRuntimeVault.PromoteIdentityForTests(
        "runtime-delay-app",
        "runtime-delay-secret-not-persisted",
        "runtime-delay-bot",
        "Runtime Delay Bot");
    delayedRuntimeVault.BindOwner("runtime-delay-owner");
    var delayedRuntimeHooks = new FeishuEventRuntimeTestHooks(
        (intendedAppId, _) => Task.FromResult(
            delayedRuntimeVault.LoadRuntimeCredentialForTests() is { } credential
            && credential.AppId.Equals(intendedAppId, StringComparison.Ordinal)
                ? credential
                : null),
        _ =>
        {
            Process child = StartFakeSidecarChild();
            delayedRuntimeChildPid = child.Id;
            return new FeishuTestSidecarSession(
                child,
                Task.Delay(TimeSpan.FromSeconds(12)));
        },
        (_, _, _) => Task.FromResult(new FeishuVerificationResult(false, "unused")),
        delayedRuntimeVault.MatchesActiveGeneration);
    await using var runtime = new FeishuEventRuntime(delayedRuntimeVault, delayedRuntimeHooks);
    Task start = runtime.StartAsync("runtime-delay-app");
    await WaitUntilAsync(
        () => runtime.Snapshot.State == FeishuEventRuntimeState.Connecting
            && delayedRuntimeChildPid != 0,
        TimeSpan.FromSeconds(3));
    await Task.Delay(TimeSpan.FromMilliseconds(10_250));
    Task identityChange = runtime.OnIntendedIdentityChangedAsync("runtime-delay-app-changed");
    bool canceled = false;
    try
    {
        await start;
    }
    catch (OperationCanceledException)
    {
        canceled = true;
    }
    await identityChange;
    Assert(
        canceled
        && !IsProcessRunning(delayedRuntimeChildPid)
        && runtime.Snapshot.State == FeishuEventRuntimeState.Stopped
        && runtime.Snapshot.RegisteredConsumerCount == 0
        && !runtime.Snapshot.CodexRouteCaptureArmed
        && !runtime.Snapshot.CodexRouteCapturePending
        && !runtime.Snapshot.ReadOnlyRouteCaptureArmed
        && !runtime.Snapshot.ReadOnlyRouteCapturePending,
        "real FeishuEventRuntime identity change during 10-20s ready must cancel start, kill its exact child, and clear every route gate");
}
finally
{
    delayedRuntimeVault.Clear();
}

string listeningRuntimeVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
var listeningRuntimeVault = new FeishuCredentialService(listeningRuntimeVaultPrefix);
int listeningRuntimeChildPid = 0;
try
{
    listeningRuntimeVault.PromoteIdentityForTests(
        "runtime-listening-app",
        "runtime-listening-secret-not-persisted",
        "runtime-listening-bot",
        "Runtime Listening Bot");
    listeningRuntimeVault.BindOwner("runtime-listening-owner");
    Assert(
        listeningRuntimeVault.SaveProjectAuthorization(WorkingDirectory).IsAuthorized,
        "runtime listening fixture must use one explicit existing project");
    var listeningRuntimeHooks = new FeishuEventRuntimeTestHooks(
        (intendedAppId, _) => Task.FromResult(
            listeningRuntimeVault.LoadRuntimeCredentialForTests() is { } credential
            && credential.AppId.Equals(intendedAppId, StringComparison.Ordinal)
                ? credential
                : null),
        _ =>
        {
            Process child = StartFakeSidecarChild();
            listeningRuntimeChildPid = child.Id;
            return new FeishuTestSidecarSession(child, Task.CompletedTask);
        },
        (_, _, _) => Task.FromResult(new FeishuVerificationResult(false, "unused")),
        listeningRuntimeVault.MatchesActiveGeneration);
    await using var runtime = new FeishuEventRuntime(listeningRuntimeVault, listeningRuntimeHooks);
    await runtime.StartAsync("runtime-listening-app");
    runtime.ArmCodexRouteCapture();
    Assert(
        runtime.Snapshot.State == FeishuEventRuntimeState.Listening
        && runtime.Snapshot.RegisteredConsumerCount == 3
        && runtime.Snapshot.ExpectedConsumerCount == 3
        && runtime.Snapshot.CodexRouteCaptureArmed,
        "real FeishuEventRuntime must expose Listening only after the exact three-consumer ready set");
    FeishuVerificationResult failedDraft = await runtime.VerifyAndSaveIdentityAsync(
        "uncommitted-draft-app",
        "uncommitted-draft-secret");
    Assert(
        !failedDraft.Success
        && !failedDraft.CredentialWriteSucceeded
        && IsProcessRunning(listeningRuntimeChildPid)
        && runtime.Snapshot.State == FeishuEventRuntimeState.Listening
        && runtime.Snapshot.OwnerBound
        && runtime.Snapshot.CodexRouteCaptureArmed
        && listeningRuntimeVault.LoadProjectAuthorization().IsAuthorized,
        "failed or cancelled credential drafts before the write boundary must preserve old runtime, owner, project, and route gates");
    await runtime.OnIntendedIdentityChangedAsync("runtime-listening-app-changed");
    Assert(
        !IsProcessRunning(listeningRuntimeChildPid)
        && runtime.Snapshot.State == FeishuEventRuntimeState.Stopped
        && runtime.Snapshot.RegisteredConsumerCount == 0
        && !runtime.Snapshot.CodexRouteCaptureArmed
        && !runtime.Snapshot.CodexRouteCapturePending,
        "real FeishuEventRuntime identity change during Listening must kill its child and atomically clear routes and consumer readiness");
}
finally
{
    listeningRuntimeVault.Clear();
}

string verifyRaceVaultPrefix = "DeepOriBridge.Windows.Tests." + Guid.NewGuid().ToString("N");
var verifyRaceVault = new FeishuCredentialService(verifyRaceVaultPrefix);
int verifyRaceChildPid = 0;
try
{
    verifyRaceVault.PromoteIdentityForTests(
        "runtime-verify-old-app",
        "runtime-verify-old-secret-not-persisted",
        "runtime-verify-old-bot",
        "Runtime Verify Old Bot");
    var verifyRaceHooks = new FeishuEventRuntimeTestHooks(
        (intendedAppId, _) => Task.FromResult(
            verifyRaceVault.LoadRuntimeCredentialForTests() is { } credential
            && credential.AppId.Equals(intendedAppId, StringComparison.Ordinal)
                ? credential
                : null),
        _ =>
        {
            Process child = StartFakeSidecarChild();
            verifyRaceChildPid = child.Id;
            return new FeishuTestSidecarSession(
                child,
                Task.Delay(TimeSpan.FromSeconds(15)));
        },
        (appId, secret, _) =>
        {
            verifyRaceVault.PromoteIdentityForTests(
                appId,
                secret,
                "runtime-verify-new-bot",
                "Runtime Verify New Bot");
            FeishuStoredCredential stored = verifyRaceVault.Load();
            return Task.FromResult(new FeishuVerificationResult(
                true,
                "verified",
                7200,
                stored.BotDisplayName,
                stored.AppFingerprint,
                CredentialWriteSucceeded: true,
                CurrentlyVerified: true));
        },
        verifyRaceVault.MatchesActiveGeneration);
    await using var runtime = new FeishuEventRuntime(verifyRaceVault, verifyRaceHooks);
    Task start = runtime.StartAsync("runtime-verify-old-app");
    await WaitUntilAsync(
        () => runtime.Snapshot.State == FeishuEventRuntimeState.Connecting
            && verifyRaceChildPid != 0,
        TimeSpan.FromSeconds(3));
    Task<FeishuVerificationResult> verify = runtime.VerifyAndSaveIdentityAsync(
        "runtime-verify-new-app",
        "runtime-verify-new-secret-not-persisted");
    bool canceled = false;
    try
    {
        await start;
    }
    catch (OperationCanceledException)
    {
        canceled = true;
    }
    FeishuVerificationResult result = await verify;
    FeishuStoredCredential active = verifyRaceVault.Load();
    Assert(
        canceled
        && result.Success
        && active.AppId == "runtime-verify-new-app"
        && active.HasVerifiedIdentity
        && !IsProcessRunning(verifyRaceChildPid)
        && runtime.Snapshot.State == FeishuEventRuntimeState.Stopped
        && runtime.Snapshot.RegisteredConsumerCount == 0,
        "real FeishuEventRuntime verify-vs-start race must kill the old child and publish only the new verified identity generation");
}
finally
{
    verifyRaceVault.Clear();
}
}

string ownerCode = FeishuOwnerBindingCodePolicy.Generate();
DateTimeOffset ownerCodeNow = DateTimeOffset.UtcNow;
Assert(ownerCode.Length == 6 && ownerCode.All(char.IsDigit), "owner binding code must be six digits");
Assert(
    FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), ownerCode, ownerCodeNow),
    "exact unexpired owner binding code must match");
Assert(
    FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), $"  {ownerCode}\r\n", ownerCodeNow),
    "owner binding code must tolerate only leading and trailing whitespace");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow, ownerCode, ownerCodeNow),
    "expired owner binding code must fail closed");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), $"{ownerCode[..3]} {ownerCode[3..]}", ownerCodeNow),
    "owner binding code must reject internal whitespace");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), $"code {ownerCode}", ownerCodeNow),
    "owner binding code must reject a six-digit substring inside other content");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), "Windows Bridge 绑定所有者", ownerCodeNow),
    "legacy static owner binding phrase must fail closed");
FeishuOwnerBindingMatchResult ownerMismatchDiagnostic = FeishuOwnerBindingCodePolicy.Evaluate(
    ownerCode,
    ownerCodeNow.AddMinutes(2),
    "not-the-code",
    ownerCodeNow);
Assert(
    !ownerMismatchDiagnostic.Matches
    && ownerMismatchDiagnostic.TrimmedLength == 12
    && !ownerMismatchDiagnostic.IsEntireSixAsciiDigits
    && !ownerMismatchDiagnostic.MatchesIgnoringExpiry
    && ownerMismatchDiagnostic.IsUnexpiredAtArrival,
    "owner mismatch diagnostic must distinguish format failure without retaining content");
FeishuOwnerBindingMatchResult ownerExpiredDiagnostic = FeishuOwnerBindingCodePolicy.Evaluate(
    ownerCode,
    ownerCodeNow,
    ownerCode,
    ownerCodeNow);
Assert(
    !ownerExpiredDiagnostic.Matches
    && ownerExpiredDiagnostic.TrimmedLength == 6
    && ownerExpiredDiagnostic.IsEntireSixAsciiDigits
    && ownerExpiredDiagnostic.MatchesIgnoringExpiry
    && !ownerExpiredDiagnostic.IsUnexpiredAtArrival,
    "owner mismatch diagnostic must distinguish an otherwise exact code that arrived expired");

var ownerGenerationGate = new FeishuOwnerBindingGate();
DateTimeOffset oldOwnerArrival = ownerCodeNow.AddSeconds(1);
FeishuOwnerBindingGateTicket oldOwnerTicket = ownerGenerationGate.Arm(
    ownerCode,
    ownerCodeNow.AddMinutes(2));
string replacementOwnerCode = ownerCode == "654321" ? "123456" : "654321";
var oldOwnerCallbackEntered = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
var releaseOldOwnerCallback = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
int oldSenderBindCount = 0;
Task<bool> delayedOldOwnerCommit = Task.Run(async () =>
{
    oldOwnerCallbackEntered.SetResult();
    await releaseOldOwnerCallback.Task;
    return ownerGenerationGate.TryCommit(
        oldOwnerTicket,
        oldOwnerArrival,
        ownerCodeNow.AddMinutes(3),
        () => Interlocked.Increment(ref oldSenderBindCount));
});
await oldOwnerCallbackEntered.Task;
Assert(
    ownerGenerationGate.TryExpire(oldOwnerTicket, ownerCodeNow.AddMinutes(2)),
    "old owner generation must expire atomically");
FeishuOwnerBindingGateTicket replacementOwnerTicket = ownerGenerationGate.Arm(
    replacementOwnerCode,
    ownerCodeNow.AddMinutes(5));
releaseOldOwnerCallback.SetResult();
Assert(
    !await delayedOldOwnerCommit
    && Volatile.Read(ref oldSenderBindCount) == 0,
    "a delayed old matching callback crossing expiry and re-arm must not consume the new generation or bind its sender");
int replacementSenderBindCount = 0;
Assert(
    ownerGenerationGate.TryCommit(
        replacementOwnerTicket,
        ownerCodeNow.AddMinutes(3).AddSeconds(1),
        ownerCodeNow.AddMinutes(3).AddSeconds(2),
        () => Interlocked.Increment(ref replacementSenderBindCount))
    && replacementSenderBindCount == 1,
    "the replacement owner generation must remain independently claimable exactly once");
Assert(
    !ownerGenerationGate.TryCommit(
        replacementOwnerTicket,
        ownerCodeNow.AddMinutes(3).AddSeconds(1),
        ownerCodeNow.AddMinutes(3).AddSeconds(2),
        () => Interlocked.Increment(ref replacementSenderBindCount))
    && replacementSenderBindCount == 1,
    "a consumed owner generation must not be reusable");

var clearedOwnerGate = new FeishuOwnerBindingGate();
FeishuOwnerBindingGateTicket clearedOldTicket = clearedOwnerGate.Arm(
    ownerCode,
    ownerCodeNow.AddMinutes(2));
var clearedOldCallbackEntered = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
var releaseClearedOldCallback = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
int clearedOldBindCount = 0;
Task<bool> delayedClearedOldCommit = Task.Run(async () =>
{
    clearedOldCallbackEntered.SetResult();
    await releaseClearedOldCallback.Task;
    return clearedOwnerGate.TryCommit(
        clearedOldTicket,
        oldOwnerArrival,
        ownerCodeNow.AddSeconds(2),
        () => Interlocked.Increment(ref clearedOldBindCount));
});
await clearedOldCallbackEntered.Task;
clearedOwnerGate.Clear();
FeishuOwnerBindingGateTicket afterClearTicket = clearedOwnerGate.Arm(
    replacementOwnerCode,
    ownerCodeNow.AddMinutes(2));
releaseClearedOldCallback.SetResult();
Assert(
    !await delayedClearedOldCommit
    && Volatile.Read(ref clearedOldBindCount) == 0,
    "a delayed old matching callback crossing clear and re-arm must not consume the new generation or bind its sender");
Assert(
    clearedOwnerGate.TryCommit(
        afterClearTicket,
        ownerCodeNow.AddSeconds(3),
        ownerCodeNow.AddSeconds(4),
        () => { }),
    "a generation armed after explicit clear must remain independently claimable");

JsonElement valid = JsonSerializer.SerializeToElement(new
{
    thread = new { id = "thread-readonly" },
    approvalPolicy = "never",
    sandbox = new { type = "readOnly" },
    cwd = WorkingDirectory,
});
var verified = CodexAppServerClient.ValidateThreadStartResponse(valid, WorkingDirectory);
Assert(verified.SandboxType == "readOnly", "readOnly response must pass");
Assert(verified.ApprovalPolicy == "never", "never response must pass");

JsonElement workspaceWrite = JsonSerializer.SerializeToElement(new
{
    thread = new { id = "thread-write" },
    approvalPolicy = "never",
    sandbox = new { type = "workspaceWrite" },
    cwd = WorkingDirectory,
});
AssertThrows(
    () => CodexAppServerClient.ValidateThreadStartResponse(workspaceWrite, WorkingDirectory),
    "workspaceWrite must fail closed");

JsonElement interactiveApproval = JsonSerializer.SerializeToElement(new
{
    thread = new { id = "thread-approval" },
    approvalPolicy = "on-request",
    sandbox = new { type = "readOnly" },
    cwd = WorkingDirectory,
});
AssertThrows(
    () => CodexAppServerClient.ValidateThreadStartResponse(interactiveApproval, WorkingDirectory),
    "non-never approval policy must fail closed");

Assert(
    FeishuEventSafetyPolicy.IsApprovedTarget("p2p", "user", "text", true, true),
    "owner candidate user event must be eligible");
Assert(
    !FeishuEventSafetyPolicy.IsApprovedTarget("p2p", "bot", "text", true, true),
    "bot event must fail closed");
Assert(
    !FeishuEventSafetyPolicy.IsApprovedTarget("p2p", "", "text", true, true),
    "missing sender type must fail closed");
Assert(
    FeishuEventSafetyPolicy.MatchesFixedCommand(
        "  Windows\u3000Codex   路由测试  ",
        "Windows Codex 路由测试"),
    "fixed commands must tolerate Unicode and repeated whitespace");
Assert(
    FeishuEventSafetyPolicy.TryExtractCanonicalText(
        "text",
        "{\"text\":\"Windows Bridge 回执测试\"}",
        out string realFeishuText,
        out string realFeishuContentState)
    && realFeishuText == "Windows Bridge 回执测试"
    && realFeishuContentState == "text-json-decoded-in-app"
    && FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        realFeishuText,
        "Windows Bridge 回执测试"),
    "official Feishu P2 text JSON must cross one canonical extraction boundary and match the exact regression command");
Assert(
    !FeishuEventSafetyPolicy.TryExtractCanonicalText(
        "post",
        "{\"text\":\"Windows Bridge 回执测试\"}",
        out _,
        out _)
    && !FeishuEventSafetyPolicy.TryExtractCanonicalText(
        "text",
        "{\"text\":\"Windows Bridge 回执测试\",\"extra\":true}",
        out _,
        out _),
    "non-text and ambiguous text envelopes must fail closed");
Assert(
    FeishuEventSafetyPolicy.TryExtractCanonicalText(
        "text",
        "Windows Bridge 回执测试",
        out string sidecarPostText,
        out string sidecarPostContentState)
    && sidecarPostText == "Windows Bridge 回执测试"
    && sidecarPostContentState == "text-plain"
    && FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        sidecarPostText,
        "Windows Bridge 回执测试"),
    "a sidecar-approved plain-text post must retain the same fail-closed command semantics");
Assert(
    FeishuEventSafetyPolicy.TryExtractReadOnlyPrompt(
        "  Windows\u3000Codex ： 检查当前项目状态  ",
        out string normalizedReadOnlyPrompt)
    && normalizedReadOnlyPrompt == "检查当前项目状态",
    "Windows Codex route must accept Unicode whitespace and full-width punctuation without broadening the prefix");
Assert(
    FeishuEventSafetyPolicy.TryExtractReadOnlyPrompt(
        "W\u200Bi\uFE0Fn\u0001dows Codex：仅回复 OK",
        out string presentationNormalizedPrompt)
    && presentationNormalizedPrompt == "仅回复 OK",
    "Windows Codex route must ignore Feishu presentation markers inside its fixed namespace");
Assert(
    FeishuEventSafetyPolicy.MatchesFixedCommand(
        "WindowsCodex\u200B路由测试",
        "Windows Codex 路由测试"),
    "fixed commands must ignore missing whitespace and zero-width format characters");
Assert(
    FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        "Win\uFE0Fdows\u0001 Bridge 回执测试",
        "Windows Bridge 回执测试"),
    "harmless reply tests must ignore invisible combining and control markers inserted by message presentation");
Assert(
    FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        "windows bridge 回执测试",
        "Windows Bridge 回执测试"),
    "harmless reply tests must tolerate Latin casing without broadening the fixed phrase");
FeishuEventSafetyPolicy.HarmlessReplyTestAnalysis wrappedReplyAnalysis =
    FeishuEventSafetyPolicy.AnalyzeHarmlessReplyTest(
        "Windows Roger Bridge 引用包装 回执测试",
        "Windows Bridge 回执测试");
Assert(
    wrappedReplyAnalysis.Matched
    && wrappedReplyAnalysis.MatchedTokenCount == 3
    && wrappedReplyAnalysis.ExpectedTokenCount == 3,
    "harmless reply tests must tolerate Feishu presentation content between ordered fixed tokens");
Assert(
    !FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        "Bridge Windows 回执测试",
        "Windows Bridge 回执测试"),
    "harmless reply test tokens must retain their fixed order");
Assert(
    !FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        "Windows Bridge 普通消息",
        "Windows Bridge 回执测试"),
    "harmless reply tests must require every fixed token");
Assert(
    FeishuEventSafetyPolicy.MatchesFixedCommand(
        "`Windows Bridge 回执测试`。",
        "Windows Bridge 回执测试"),
    "fixed commands must tolerate common Feishu copy, inline-code, quote, and punctuation wrappers");
Assert(
    FeishuEventSafetyPolicy.MatchesFixedCommand(
        "“Windows Bridge 回执测试”",
        "Windows Bridge 回执测试"),
    "fixed commands must tolerate Chinese presentation quotes without weakening text equality");
Assert(
    FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        "已发送：Windows Bridge 回执测试，请确认。",
        "Windows Bridge 回执测试"),
    "harmless reply test may be embedded in a short Feishu presentation envelope");
Assert(
    !FeishuEventSafetyPolicy.MatchesHarmlessReplyTest(
        new string('x', 513) + "Windows Bridge 回执测试",
        "Windows Bridge 回执测试"),
    "harmless reply test envelope must be bounded");
Assert(
    !FeishuEventSafetyPolicy.MatchesFixedCommand(
        "Windows Codex 路由测试 extra",
        "Windows Codex 路由测试"),
    "fixed commands must reject additional non-whitespace content");
Assert(
    FeishuEventSafetyPolicy.CanEnterControlledRouteGate(
        "unmatched", approvedTarget: true, ownerMatches: true),
    "armed controlled route may admit an unmatched owner message to the manual gate");
Assert(
    !FeishuEventSafetyPolicy.CanEnterControlledRouteGate(
        "unmatched", approvedTarget: true, ownerMatches: false),
    "manual route fallback must reject a non-owner message");
Assert(
    FeishuEventSafetyPolicy.CanSendUnmatchedOwnerHelp(
        "unmatched",
        approvedTarget: true,
        ownerMatches: true,
        controlledCaptured: false,
        readOnlyCaptured: false),
    "unmatched approved owner text must receive safe help instead of silent failure");
Assert(
    !FeishuEventSafetyPolicy.CanSendUnmatchedOwnerHelp(
        "unmatched",
        approvedTarget: true,
        ownerMatches: false,
        controlledCaptured: false,
        readOnlyCaptured: false),
    "unmatched non-owner text must not receive a reply");
Assert(
    !FeishuEventSafetyPolicy.CanSendUnmatchedOwnerHelp(
        "unmatched",
        approvedTarget: true,
        ownerMatches: true,
        controlledCaptured: true,
        readOnlyCaptured: false),
    "unmatched help must not race a controlled route capture");
Assert(
    !FeishuEventSafetyPolicy.CanEnterControlledRouteGate(
        "reply-test", approvedTarget: true, ownerMatches: true),
    "other fixed actions must not be diverted into the controlled route gate");

DateTimeOffset gateNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
using (var automaticReadOnlyGates = NewRouteGates())
{
    FeishuRouteCaptureResult capture = automaticReadOnlyGates.ProcessMessage(
        "automatic-readonly-message",
        "codex-readonly",
        "检查当前项目状态",
        approvedTarget: true,
        ownerMatches: true,
        gateNow,
        @"D:\Authorized\ExactProject",
        allowAutomaticReadOnly: true);
    Assert(capture.ReadOnlyCaptured,
        "an owner message with an exact project authorization must enter the automatic read-only route without arming");
    FeishuRouteAuthorization authorization = automaticReadOnlyGates.Claim(
        FeishuRouteKind.ReadOnly,
        gateNow);
    Assert(authorization.Prompt == "检查当前项目状态"
        && authorization.ProjectPath == @"D:\Authorized\ExactProject",
        "automatic read-only authorization must preserve the prompt and exact project path");
}
using (var automaticReadOnlyGates = NewRouteGates())
{
    FeishuRouteCaptureResult noProject = automaticReadOnlyGates.ProcessMessage(
        "automatic-readonly-no-project",
        "codex-readonly",
        "检查当前项目状态",
        approvedTarget: true,
        ownerMatches: true,
        gateNow,
        projectPath: string.Empty,
        allowAutomaticReadOnly: true);
    Assert(!noProject.ReadOnlyCaptured,
        "automatic read-only routing must fail closed without an exact project path");

    FeishuRouteCaptureResult nonOwner = automaticReadOnlyGates.ProcessMessage(
        "automatic-readonly-non-owner",
        "codex-readonly",
        "检查当前项目状态",
        approvedTarget: true,
        ownerMatches: false,
        gateNow,
        @"D:\Authorized\ExactProject",
        allowAutomaticReadOnly: true);
    Assert(!nonOwner.ReadOnlyCaptured,
        "automatic read-only routing must reject a non-owner sender");

    FeishuRouteCaptureResult unmatched = automaticReadOnlyGates.ProcessMessage(
        "automatic-readonly-unmatched",
        "unmatched",
        "检查当前项目状态",
        approvedTarget: true,
        ownerMatches: true,
        gateNow,
        @"D:\Authorized\ExactProject",
        allowAutomaticReadOnly: true);
    Assert(!unmatched.ReadOnlyCaptured,
        "automatic routing must never treat an unmatched message as a read-only request");
}
using (var projectGates = NewRouteGates())
{
    projectGates.Arm(FeishuRouteKind.ReadOnly);
    FeishuRouteCaptureResult capture = projectGates.ProcessMessage(
        "project-bound-message",
        "codex-readonly",
        "只读检查",
        approvedTarget: true,
        ownerMatches: true,
        gateNow,
        @"D:\Authorized\ExactProject");
    Assert(capture.ReadOnlyCaptured, "authorized sender project context must be captured");
    FeishuRouteAuthorization authorization = projectGates.Claim(FeishuRouteKind.ReadOnly, gateNow);
    Assert(authorization.ProjectPath == @"D:\Authorized\ExactProject",
        "claimed route must preserve the exact sender-specific project path");
}
using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "synthetic-message-a",
        "codex-route-test",
        null,
        approvedTarget: true,
        ownerMatches: true,
        gateNow);
    Assert(capture.ControlledCaptured, "controlled route must capture one eligible owner message");
    FeishuRouteAuthorization authorization = gates.Claim(FeishuRouteKind.Controlled, gateNow);
    AssertThrows(
        () => gates.Claim(FeishuRouteKind.Controlled, gateNow),
        "a claimed route authorization must reject a second execution");
    gates.Complete(authorization);
    Assert(!gates.State.ControlledPending, "completed route must clear its in-memory ticket");
}

var armedClock119 = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(armedClock119))
{
    gates.Arm(FeishuRouteKind.Controlled);
    armedClock119.Advance(TimeSpan.FromSeconds(119));
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "armed-119-message", "codex-route-test", null, true, true, gateNow.AddSeconds(119));
    Assert(capture.ControlledCaptured,
        "an armed Gate must still capture at 119 seconds");
}

var armedClockExpired = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(armedClockExpired))
{
    FeishuRouteGateState armed = gates.Arm(FeishuRouteKind.Controlled);
    armedClockExpired.Advance(TimeSpan.FromSeconds(120));
    FeishuRouteCaptureResult expiredCapture = gates.ProcessMessage(
        "armed-expired-message", "codex-route-test", null, true, true, gateNow.AddSeconds(120));
    FeishuRouteGateState expiredState = gates.State;
    Assert(!expiredCapture.ControlledCaptured,
        "an armed Gate must reject a message at 120 seconds");
    Assert(!expiredState.ControlledArmed
        && !expiredState.ControlledPending
        && !expiredState.ControlledClaimed
        && expiredState.ReadOnlyPrompt is null,
        "armed expiry must atomically clear armed, pending, claim, and route data");
    AssertThrows(
        () => gates.Claim(FeishuRouteKind.Controlled, gateNow.AddSeconds(120)),
        "an expired armed Gate must have no claimable ticket");
    Assert(!gates.ExpireArmed(
        FeishuRouteKind.Controlled,
        armed.Generation,
        armed.ControlledArmVersion),
        "an already expired Gate callback must be idempotent");
}

var armedClockRearm = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(armedClockRearm))
{
    FeishuRouteGateState oldArm = gates.Arm(FeishuRouteKind.Controlled);
    armedClockRearm.Advance(TimeSpan.FromSeconds(60));
    gates.Clear(FeishuRouteKind.Controlled);
    FeishuRouteGateState newArm = gates.Arm(FeishuRouteKind.Controlled);
    armedClockRearm.Advance(TimeSpan.FromSeconds(60));
    Assert(!gates.ExpireArmed(
        FeishuRouteKind.Controlled,
        oldArm.Generation,
        oldArm.ControlledArmVersion),
        "an old expiry callback must not match a re-armed Gate");
    Assert(gates.State.ControlledArmed
        && gates.State.ControlledArmVersion == newArm.ControlledArmVersion,
        "an old expiry callback must not clear the new Gate");
}

var armedClockConcurrent = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(armedClockConcurrent))
{
    gates.Arm(FeishuRouteKind.Controlled);
    armedClockConcurrent.Advance(TimeSpan.FromSeconds(121));
    int captures = 0;
    Parallel.For(0, 100, index =>
    {
        FeishuRouteCaptureResult result = gates.ProcessMessage(
            $"expired-concurrent-{index}",
            "codex-route-test",
            null,
            true,
            true,
            gateNow.AddSeconds(121));
        if (result.ControlledCaptured)
        {
            Interlocked.Increment(ref captures);
        }
    });
    Assert(captures == 0,
        "all concurrent captures after armed expiry must be rejected");
    Assert(!gates.State.ControlledArmed && !gates.State.ControlledPending,
        "concurrent expiry must remain atomically fail closed");
}

var independentTicketClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(independentTicketClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    independentTicketClock.Advance(TimeSpan.FromSeconds(119));
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "independent-ticket-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow.AddSeconds(119));
    Assert(capture.ControlledCaptured,
        "capture before armed expiry must create a fresh authorization ticket");
    FeishuRouteAuthorization authorization = gates.Claim(
        FeishuRouteKind.Controlled,
        gateNow.AddSeconds(238));
    Assert(authorization.ExpiresAt == gateNow.AddSeconds(239),
        "authorization TTL must start at capture and remain independent of armed TTL");
    AssertThrows(
        () => gates.Claim(FeishuRouteKind.Controlled, gateNow.AddSeconds(238)),
        "the independently valid authorization ticket must still be one-time claim");
}

using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult first = gates.ProcessMessage(
        "synthetic-message-b", "codex-route-test", null, true, true, gateNow);
    Assert(first.ControlledCaptured, "first delivery must be captured");
    gates.Clear(FeishuRouteKind.Controlled);
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult duplicate = gates.ProcessMessage(
        "synthetic-message-b", "codex-route-test", null, true, true, gateNow.AddSeconds(1));
    Assert(duplicate.Duplicate && !duplicate.ControlledCaptured,
        "redelivery of one message id must be deduplicated in memory");
}

using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage("synthetic-message-c", "codex-route-test", null, true, true, gateNow);
    FeishuRouteAuthorization authorization = gates.Claim(FeishuRouteKind.Controlled, gateNow);
    gates.SetListening(false);
    Assert(authorization.SessionCancellation.IsCancellationRequested,
        "transport loss must cancel the session token");
    Assert(!gates.State.ControlledPending && !gates.State.ControlledArmed,
        "transport loss must clear every controlled route gate");
    AssertThrows(
        () => gates.Complete(authorization),
        "authorization from an old generation must not complete after disconnect");
}

var readOnlyAuthorizationClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(readOnlyAuthorizationClock))
{
    gates.Arm(FeishuRouteKind.ReadOnly);
    gates.ProcessMessage(
        "synthetic-message-d",
        "codex-readonly",
        "safe prompt",
        true,
        true,
        gateNow);
    readOnlyAuthorizationClock.Advance(TimeSpan.FromSeconds(120));
    AssertThrows(
        () => gates.Claim(FeishuRouteKind.ReadOnly, gateNow.AddSeconds(121)),
        "a route authorization must expire after 120 seconds");
    Assert(!gates.State.ReadOnlyPending, "expired route ticket must be cleared");
}

using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage("synthetic-message-e", "codex-route-test", null, true, true, gateNow);
    gates.SetOwnerBound(false);
    Assert(!gates.State.ControlledPending && !gates.State.ControlledArmed,
        "owner removal must clear all route authorization state");
}

using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage("synthetic-message-f", "codex-route-test", null, true, true, gateNow);
    FeishuRouteAuthorization authorization = gates.Claim(FeishuRouteKind.Controlled, gateNow);
    for (int index = 0; index < 100; index++)
    {
        gates.Invalidate(ownerBound: true, listening: false);
        gates.SetListening(true);
    }
    Assert(authorization.SessionCancellation.IsCancellationRequested,
        "reconnect churn must keep the old generation cancelled");
    Assert(gates.RetiredSessionCount <= FeishuRouteGateCoordinator.RetiredSessionCapacity,
        "retired session cancellation sources must remain strictly bounded");
    AssertThrows(
        () => gates.Complete(authorization),
        "reconnect churn must never revive an old authorization");
}

var expiredClaimClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(expiredClaimClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage(
        "expired-projection-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    expiredClaimClock.Advance(TimeSpan.FromSeconds(121));
    var projectedStore = new FeishuEventSnapshotStore(
        new FeishuEventRuntimeSnapshot(
            FeishuEventRuntimeState.Listening,
            CodexRouteCapturePending: true),
        snapshot => snapshot);
    int projectionCallbacks = 0;
    string failureReason = string.Empty;
    try
    {
        FeishuRouteClaimProjection.ClaimAndProject(
            () => gates.Claim(FeishuRouteKind.Controlled, gateNow.AddSeconds(121)),
            () => gates.State,
            (state, _) =>
            {
                Interlocked.Increment(ref projectionCallbacks);
                projectedStore.Update(snapshot => snapshot with
                {
                    CodexRouteCapturePending = state.ControlledPending
                        && !state.ControlledClaimed,
                });
            });
    }
    catch (InvalidOperationException exception)
    {
        failureReason = exception.Message;
    }

    FeishuRouteGateState state = gates.State;
    Assert(failureReason.Contains("120", StringComparison.Ordinal),
        "expired claim must preserve the original fail-closed reason");
    Assert(projectionCallbacks == 1,
        "expired claim must project coordinator state exactly once");
    Assert(!state.ControlledPending && !state.ControlledClaimed,
        "expired claim must clear pending and claimed coordinator state");
    Assert(!projectedStore.Read().CodexRouteCapturePending,
        "expired claim projection must immediately disable Execute");
}

var authorizationExpiryClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(authorizationExpiryClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "authorization-expiry-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    authorizationExpiryClock.Advance(TimeSpan.FromSeconds(119));
    Assert(!gates.ExpireAuthorization(
        FeishuRouteKind.Controlled,
        capture.State.Generation,
        capture.State.ControlledArmVersion),
        "authorization callback must not expire a ticket at 119 seconds");
    Assert(gates.State.ControlledPending && !gates.State.ControlledClaimed,
        "authorization must remain executable before its monotonic TTL");
    authorizationExpiryClock.Advance(TimeSpan.FromSeconds(1));
    Assert(gates.ExpireAuthorization(
        FeishuRouteKind.Controlled,
        capture.State.Generation,
        capture.State.ControlledArmVersion),
        "authorization callback must expire an unclaimed ticket at 120 seconds");
    Assert(!gates.State.ControlledPending && !gates.State.ControlledClaimed,
        "authorization expiry must atomically clear pending and claimed state");
}

var staleAuthorizationClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(staleAuthorizationClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult first = gates.ProcessMessage(
        "stale-authorization-first",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    staleAuthorizationClock.Advance(TimeSpan.FromSeconds(60));
    gates.Clear(FeishuRouteKind.Controlled);
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult second = gates.ProcessMessage(
        "stale-authorization-second",
        "codex-route-test",
        null,
        true,
        true,
        gateNow.AddSeconds(60));
    staleAuthorizationClock.Advance(TimeSpan.FromSeconds(60));
    Assert(!gates.ExpireAuthorization(
        FeishuRouteKind.Controlled,
        first.State.Generation,
        first.State.ControlledArmVersion),
        "an old authorization callback must not clear a newer ticket");
    Assert(gates.State.ControlledPending
        && gates.State.ControlledArmVersion == second.State.ControlledArmVersion,
        "the newer authorization must survive an old callback");
}

var claimedAuthorizationClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(claimedAuthorizationClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "claimed-authorization-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    gates.Claim(FeishuRouteKind.Controlled, gateNow);
    claimedAuthorizationClock.Advance(TimeSpan.FromSeconds(120));
    Assert(!gates.ExpireAuthorization(
        FeishuRouteKind.Controlled,
        capture.State.Generation,
        capture.State.ControlledArmVersion),
        "authorization timer must not clear an already claimed operation");
    Assert(gates.State.ControlledClaimed,
        "a claimed operation remains owned by its execution completion path");
}

var invalidatedAuthorizationClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(invalidatedAuthorizationClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "invalidated-authorization-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    gates.SetListening(false);
    invalidatedAuthorizationClock.Advance(TimeSpan.FromSeconds(120));
    Assert(!gates.ExpireAuthorization(
        FeishuRouteKind.Controlled,
        capture.State.Generation,
        capture.State.ControlledArmVersion),
        "authorization callback from an invalidated generation must be inert");
    Assert(!gates.State.ControlledPending && !gates.State.ControlledClaimed,
        "transport invalidation must remain fail closed after a stale callback");
}

using (var gates = NewRouteGates())
{
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage(
        "duplicate-claim-projection-message",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    FeishuRouteClaimProjection.ClaimAndProject(
        () => gates.Claim(FeishuRouteKind.Controlled, gateNow),
        () => gates.State,
        (state, failure) =>
        {
            Assert(failure is null && state.ControlledClaimed,
                "successful claim projection must expose claimed state");
            Assert(!state.ControlledArmed,
                "successful claim projection must not re-arm the Gate");
        });

    int duplicateProjectionCallbacks = 0;
    AssertThrows(
        () => FeishuRouteClaimProjection.ClaimAndProject(
            () => gates.Claim(FeishuRouteKind.Controlled, gateNow),
            () => gates.State,
            (state, failure) =>
            {
                duplicateProjectionCallbacks++;
                Assert(failure is InvalidOperationException,
                    "duplicate claim projection must receive the claim failure");
                Assert(state.ControlledClaimed,
                    "duplicate claim must remain one-time claimed");
            }),
        "duplicate claim must still fail closed after projection");
    Assert(duplicateProjectionCallbacks == 1,
        "duplicate claim must project final state exactly once");
}

for (int iteration = 0; iteration < 50; iteration++)
{
    using var gates = NewRouteGates();
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage(
        $"owner-claim-race-{iteration}",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    using var start = new ManualResetEventSlim();
    Task claim = Task.Run(() =>
    {
        start.Wait();
        try
        {
            FeishuRouteClaimProjection.ClaimAndProject(
                () => gates.Claim(FeishuRouteKind.Controlled, gateNow),
                () => gates.State,
                (_, _) => { });
        }
        catch (InvalidOperationException)
        {
        }
    });
    Task invalidate = Task.Run(() =>
    {
        start.Wait();
        gates.SetOwnerBound(false);
    });
    start.Set();
    await Task.WhenAll(claim, invalidate);
    FeishuRouteGateState final = gates.State;
    Assert(!final.ControlledArmed
        && !final.ControlledPending
        && !final.ControlledClaimed,
        "owner invalidation racing claim must finish fail closed");
}

for (int iteration = 0; iteration < 50; iteration++)
{
    using var gates = NewRouteGates();
    gates.Arm(FeishuRouteKind.Controlled);
    gates.ProcessMessage(
        $"transport-claim-race-{iteration}",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    using var start = new ManualResetEventSlim();
    Task claim = Task.Run(() =>
    {
        start.Wait();
        try
        {
            FeishuRouteClaimProjection.ClaimAndProject(
                () => gates.Claim(FeishuRouteKind.Controlled, gateNow),
                () => gates.State,
                (_, _) => { });
        }
        catch (InvalidOperationException)
        {
        }
    });
    Task invalidate = Task.Run(() =>
    {
        start.Wait();
        gates.SetListening(false);
    });
    start.Set();
    await Task.WhenAll(claim, invalidate);
    FeishuRouteGateState final = gates.State;
    Assert(!final.ControlledArmed
        && !final.ControlledPending
        && !final.ControlledClaimed,
        "transport invalidation racing claim must finish fail closed");
}

var expiryScheduler = new FeishuArmExpiryScheduler();
int expiryCallbacks = 0;
Task[] expiryPressure = Enumerable.Range(0, 8)
    .Select(worker => Task.Run(() =>
    {
        for (int iteration = 0; iteration < 250; iteration++)
        {
            FeishuRouteKind kind = (worker + iteration) % 2 == 0
                ? FeishuRouteKind.Controlled
                : FeishuRouteKind.ReadOnly;
            expiryScheduler.Schedule(
                kind,
                TimeSpan.FromMilliseconds(5),
                () => Interlocked.Increment(ref expiryCallbacks));
            if (iteration % 3 == 0)
            {
                expiryScheduler.Cancel(kind);
            }
        }
    }))
    .ToArray();
await Task.WhenAll(expiryPressure);
expiryScheduler.Schedule(
    FeishuRouteKind.Controlled,
    TimeSpan.FromMilliseconds(1),
    () => Interlocked.Increment(ref expiryCallbacks));
await Task.Delay(20);
await expiryScheduler.DisposeAsync();
await expiryScheduler.DisposeAsync();
Assert(expiryScheduler.ActiveCount == 0,
    "expiry scheduler shutdown must await every active task");
Assert(expiryScheduler.FaultCount == 0,
    "expiry scheduler pressure must not produce ODE or unobserved task faults");
Assert(expiryCallbacks > 0,
    "expiry scheduler must still run the current non-cancelled callback");

var automaticAuthorizationClock = new ManualTimeProvider(gateNow);
using (var gates = NewRouteGates(automaticAuthorizationClock))
{
    gates.Arm(FeishuRouteKind.Controlled);
    FeishuRouteCaptureResult capture = gates.ProcessMessage(
        "automatic-authorization-expiry",
        "codex-route-test",
        null,
        true,
        true,
        gateNow);
    var projectedStore = new FeishuEventSnapshotStore(
        new FeishuEventRuntimeSnapshot(
            FeishuEventRuntimeState.Listening,
            CodexRouteCapturePending: true),
        snapshot => snapshot);
    await using var scheduler = new FeishuArmExpiryScheduler();
    var callbackCompleted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    scheduler.Schedule(
        FeishuRouteKind.Controlled,
        TimeSpan.FromMilliseconds(10),
        () =>
        {
            if (gates.ExpireAuthorization(
                FeishuRouteKind.Controlled,
                capture.State.Generation,
                capture.State.ControlledArmVersion))
            {
                FeishuRouteGateState state = gates.State;
                projectedStore.Update(snapshot => snapshot with
                {
                    CodexRouteCapturePending = state.ControlledPending
                        && !state.ControlledClaimed,
                });
            }

            callbackCompleted.TrySetResult();
        });
    automaticAuthorizationClock.Advance(TimeSpan.FromSeconds(120));
    await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(!projectedStore.Read().CodexRouteCapturePending,
        "authorization timer must automatically project Execute disabled at expiry");
    Assert(scheduler.FaultCount == 0,
        "authorization expiry projection must complete without scheduler faults");
}

bool projectedControlledGate = true;
var snapshotStore = new FeishuEventSnapshotStore(
    new FeishuEventRuntimeSnapshot(FeishuEventRuntimeState.Listening),
    snapshot => snapshot with { CodexRouteCaptureArmed = projectedControlledGate });
Parallel.For(0, 1000, index =>
{
    snapshotStore.Update(snapshot => snapshot with
    {
        ReceivedEventCount = snapshot.ReceivedEventCount + 1,
        ReplySentCount = snapshot.ReplySentCount + 1,
        LastMatchDiagnostic = "synthetic-concurrency-diagnostic",
    });
});
FeishuEventRuntimeSnapshot concurrentSnapshot = snapshotStore.Read();
Assert(concurrentSnapshot.ReceivedEventCount == 1000,
    "snapshot event count updates must not be lost under concurrency");
Assert(concurrentSnapshot.ReplySentCount == 1000,
    "snapshot reply updates must not be lost under concurrency");
Assert(concurrentSnapshot.LastMatchDiagnostic == "synthetic-concurrency-diagnostic",
    "snapshot diagnostics must survive concurrent updates");
Assert(concurrentSnapshot.CodexRouteCaptureArmed,
    "snapshot store must apply the current Gate projection inside the same lock");

foreach (string rawId in new[] { "17", "\"opaque-request-id\"", "0" })
{
    using JsonDocument requestIdDocument = JsonDocument.Parse(rawId);
    string responseJson = CodexAppServerClient.BuildRejectedServerRequestResponse(
        requestIdDocument.RootElement,
        "item/commandExecution/requestApproval");
    using JsonDocument response = JsonDocument.Parse(responseJson);
    AssertJsonRpcIdPreserved(
        requestIdDocument.RootElement,
        response.RootElement.GetProperty("id"),
        $"JSON-RPC id {rawId} must preserve its ValueKind and value");
    Assert(response.RootElement.GetProperty("jsonrpc").GetString() == "2.0",
        "server-request rejection must remain a JSON-RPC 2.0 response");
}

var rejectedServerRequests = new[]
{
    (Method: "item/commandExecution/requestApproval", Decision: "cancel"),
    (Method: "item/fileChange/requestApproval", Decision: "cancel"),
    (Method: "execCommandApproval", Decision: "abort"),
    (Method: "applyPatchApproval", Decision: "abort"),
};
foreach ((string method, string decision) in rejectedServerRequests)
{
    using JsonDocument requestIdDocument = JsonDocument.Parse("23");
    using JsonDocument response = JsonDocument.Parse(
        CodexAppServerClient.BuildRejectedServerRequestResponse(
            requestIdDocument.RootElement,
            method));
    Assert(response.RootElement.GetProperty("result").GetProperty("decision").GetString()
        == decision,
        $"{method} must fail closed with {decision}");
    AssertJsonRpcIdPreserved(
        requestIdDocument.RootElement,
        response.RootElement.GetProperty("id"),
        $"{method} must preserve the request id");
}

using (JsonDocument requestIdDocument = JsonDocument.Parse("\"permission-request\""))
using (JsonDocument permissionResponse = JsonDocument.Parse(
    CodexAppServerClient.BuildRejectedServerRequestResponse(
        requestIdDocument.RootElement,
        "item/permissions/requestApproval")))
{
    JsonElement permissions = permissionResponse.RootElement
        .GetProperty("result")
        .GetProperty("permissions");
    Assert(permissions.GetProperty("fileSystem").ValueKind == JsonValueKind.Null
        && permissions.GetProperty("network").ValueKind == JsonValueKind.Null,
        "permission request must grant neither file-system nor network permissions");
    Assert(permissionResponse.RootElement.GetProperty("result").GetProperty("scope").ValueKind
        == JsonValueKind.Null,
        "permission request must grant no scope");
    AssertJsonRpcIdPreserved(
        requestIdDocument.RootElement,
        permissionResponse.RootElement.GetProperty("id"),
        "permission rejection must preserve a string request id");
}

using (JsonDocument requestIdDocument = JsonDocument.Parse("0"))
using (JsonDocument unknownResponse = JsonDocument.Parse(
    CodexAppServerClient.BuildRejectedServerRequestResponse(
        requestIdDocument.RootElement,
        "item/tool/requestUserInput")))
{
    Assert(unknownResponse.RootElement.GetProperty("error").GetProperty("code").GetInt32()
        == -32001,
        "unknown server-initiated input must receive a fail-closed JSON-RPC error");
    AssertJsonRpcIdPreserved(
        requestIdDocument.RootElement,
        unknownResponse.RootElement.GetProperty("id"),
        "unknown request rejection must preserve id 0");
}

IReadOnlyList<JsonElement> concatenated = CodexAppServerClient.ParseStdioMessages(
    "{\"method\":\"thread/status/changed\",\"params\":{}}{\"id\":2,\"result\":{\"data\":[]}}");
Assert(concatenated.Count == 2, "concatenated stdio messages must be split");
Assert(
    concatenated[0].GetProperty("method").GetString() == "thread/status/changed"
    && concatenated[1].GetProperty("id").GetInt32() == 2,
    "split stdio messages must preserve order and content");

Assert(CodexAppServerClient.IsTerminalTurnStatus("completed"),
    "completed must be a terminal turn status");
Assert(CodexAppServerClient.IsTerminalTurnStatus("interrupted"),
    "interrupted must be a terminal turn status");
Assert(CodexAppServerClient.IsTerminalTurnStatus("failed"),
    "failed must be a terminal turn status");
Assert(!CodexAppServerClient.IsTerminalTurnStatus("inProgress"),
    "inProgress must not be treated as terminal");

const string sidecarRequestId = "0123456789abcdef0123456789abcdef";
const string syntheticMessageReference = "synthetic-sensitive-message-reference";
const string syntheticReplyBody = "synthetic-sensitive-reply-body";
string sidecarCommand = FeishuSidecarProtocol.BuildReplyCommandJson(
    sidecarRequestId,
    syntheticMessageReference,
    syntheticReplyBody,
    "synthetic-idempotency-key");
using JsonDocument sidecarCommandDocument = JsonDocument.Parse(sidecarCommand);
Assert(sidecarCommandDocument.RootElement.GetProperty("type").GetString() == "reply",
    "sidecar reply command must use the stdin-only reply command type");
Assert(sidecarCommandDocument.RootElement.GetProperty("request_id").GetString() == sidecarRequestId,
    "sidecar reply command must use an opaque correlation id");
using JsonDocument sidecarResultDocument = JsonDocument.Parse(
    $"{{\"type\":\"command_result\",\"request_id\":\"{sidecarRequestId}\",\"state\":\"succeeded\"}}");
Assert(
    FeishuSidecarProtocol.TryReadCommandResult(
        sidecarResultDocument.RootElement,
        out string parsedRequestId,
        out FeishuSidecarCommandResult sidecarResult)
    && parsedRequestId == sidecarRequestId
    && sidecarResult.Succeeded,
    "sidecar command result must contain only opaque correlation and status");
Assert(
    !sidecarResultDocument.RootElement.GetRawText().Contains(syntheticMessageReference, StringComparison.Ordinal)
    && !sidecarResultDocument.RootElement.GetRawText().Contains(syntheticReplyBody, StringComparison.Ordinal),
    "sidecar stdout command result must not echo message references or reply bodies");
using JsonDocument failedSidecarResultDocument = JsonDocument.Parse(
    $"{{\"type\":\"command_result\",\"request_id\":\"{sidecarRequestId}\",\"state\":\"failed\",\"error_stage\":\"api\",\"error_code\":230001}}");
Assert(
    FeishuSidecarProtocol.TryReadCommandResult(
        failedSidecarResultDocument.RootElement,
        out _,
        out FeishuSidecarCommandResult failedSidecarResult)
    && !failedSidecarResult.Succeeded
    && failedSidecarResult.ErrorStage == "api"
    && failedSidecarResult.ErrorCode == 230001,
    "sidecar failures must retain only a safe stage and numeric API code");
using JsonDocument validationSidecarResultDocument = JsonDocument.Parse(
    $"{{\"type\":\"command_result\",\"request_id\":\"{sidecarRequestId}\",\"state\":\"failed\",\"error_stage\":\"api\",\"error_code\":99992402,\"error_diagnostic\":\"field_validation_failed;bot.bot_menus[0].menu_id:length\"}}");
Assert(
    FeishuSidecarProtocol.TryReadCommandResult(
        validationSidecarResultDocument.RootElement,
        out _,
        out FeishuSidecarCommandResult validationSidecarResult)
    && validationSidecarResult.ErrorCode == 99992402
    && validationSidecarResult.ErrorDiagnostic
        == "field_validation_failed;bot.bot_menus[0].menu_id:length",
    "field-validation failures must retain only a safe field path and rule category");
using JsonDocument unsafeValidationSidecarResultDocument = JsonDocument.Parse(
    $"{{\"type\":\"command_result\",\"request_id\":\"{sidecarRequestId}\",\"state\":\"failed\",\"error_stage\":\"api\",\"error_code\":99992402,\"error_diagnostic\":\"secret value leaked\"}}");
Assert(
    FeishuSidecarProtocol.TryReadCommandResult(
        unsafeValidationSidecarResultDocument.RootElement,
        out _,
        out FeishuSidecarCommandResult unsafeValidationSidecarResult)
    && unsafeValidationSidecarResult.ErrorDiagnostic.Length == 0,
    "unsafe validation diagnostics must be discarded at the C# protocol boundary");
string replyIdempotencyKey = FeishuEventRuntime.BuildIdempotencyKey(
    syntheticMessageReference,
    "deepori-win-unmatched-help");
Assert(replyIdempotencyKey.Length == 36 && replyIdempotencyKey.StartsWith("dbw-", StringComparison.Ordinal),
    "reply idempotency keys must remain within the UUID-sized API-safe envelope");
Assert(!replyIdempotencyKey.Contains(syntheticMessageReference, StringComparison.Ordinal),
    "reply idempotency keys must not expose message references");
string p2pCommand = FeishuSidecarProtocol.BuildSendP2PCommandJson(
    sidecarRequestId,
    "synthetic-owner-open-id",
    "synthetic-menu-response",
    "synthetic-menu-idempotency-key");
using JsonDocument p2pCommandDocument = JsonDocument.Parse(p2pCommand);
Assert(
    p2pCommandDocument.RootElement.GetProperty("type").GetString() == "send_p2p"
    && p2pCommandDocument.RootElement.GetProperty("recipient_id").GetString() == "synthetic-owner-open-id",
    "menu callback replies must use a dedicated P2P SDK command because menu events have no message to reply to");
string menuCommand = FeishuSidecarProtocol.BuildMenuCommandJson(
    FeishuSidecarProtocol.MenuInspectApplyCommandType,
    sidecarRequestId,
    new string('a', 32));
using JsonDocument menuCommandDocument = JsonDocument.Parse(menuCommand);
Assert(
    menuCommandDocument.RootElement.GetProperty("type").GetString() == "menu_inspect_apply"
    && menuCommandDocument.RootElement.GetProperty("expected_identity_generation").GetString() == new string('a', 32),
    "automatic menu writes must bind every command to the verified identity generation");
string readOnlyMenuCommand = FeishuSidecarProtocol.BuildMenuCommandJson(
    FeishuSidecarProtocol.MenuInspectCommandType,
    sidecarRequestId,
    new string('a', 32));
using JsonDocument readOnlyMenuCommandDocument = JsonDocument.Parse(readOnlyMenuCommand);
Assert(
    readOnlyMenuCommandDocument.RootElement.GetProperty("type").GetString() == "menu_inspect",
    "ordinary startup must have a protocol-level menu inspection command that cannot select apply or publish");
using JsonDocument menuResultDocument = JsonDocument.Parse(
    $"{{\"type\":\"command_result\",\"request_id\":\"{sidecarRequestId}\",\"state\":\"succeeded\",\"operation\":\"menu_inspect_apply\",\"menu_stage\":\"written_pending_publish\",\"menu_diff\":\"managed_drift\",\"version_state\":\"written_pending_publish\",\"version\":\"1.0.1\"}}");
Assert(
    FeishuSidecarProtocol.TryReadCommandResult(
        menuResultDocument.RootElement,
        out _,
        out FeishuSidecarCommandResult menuResult)
    && menuResult.Succeeded
    && menuResult.MenuStage == "written_pending_publish"
    && menuResult.MenuDiff == "managed_drift"
    && menuResult.Version == "1.0.1",
    "menu command result must carry only structured, non-sensitive stage and diff metadata");

var readyExpectation = new FeishuSidecarReadyExpectation(
    new string('1', 64),
    new string('2', 64),
    new string('3', 32),
    4100,
    638900000000000000,
    new string('4', 64),
    new string('5', 64),
    FeishuSidecarProtocol.ExpectedConsumers,
    new string('6', 64));
string sidecarToolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");
using JsonDocument sidecarManifestDocument = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(sidecarToolsDirectory, "feishu-event-sidecar.manifest.json")));
string sidecarManifestHash = sidecarManifestDocument.RootElement
    .GetProperty("sha256")
    .GetString()!;
string sidecarDiskHash = FileAttestation.Sha256(Path.Combine(
    sidecarToolsDirectory,
    "feishu-event-sidecar.exe"));
string generatedSidecarAttestation = FeishuSidecarBuildAttestation.ExpectedSha256;
Assert(
    FileAttestation.FixedTimeEquals(sidecarManifestHash, sidecarDiskHash)
    && generatedSidecarAttestation.Contains(sidecarManifestHash, StringComparison.Ordinal),
    "sidecar disk SHA-256 must match both manifest and C# assembly build attestation source");
JsonElement exactReady = JsonSerializer.SerializeToElement(new
{
    type = "lifecycle",
    state = "ready",
    runtime_nonce = readyExpectation.RuntimeNonce,
    identity_fingerprint = readyExpectation.IdentityFingerprint,
    identity_generation = readyExpectation.IdentityGeneration,
    parent_pid = readyExpectation.ParentProcessId,
    parent_start_time_utc_ticks = readyExpectation.ParentStartTimeUtcTicks,
    parent_executable_sha256 = readyExpectation.ParentExecutableSha256,
    parent_payload_manifest_sha256 = readyExpectation.ParentPayloadManifestSha256,
    registered_consumers = FeishuSidecarProtocol.ExpectedConsumers,
    sidecar_executable_sha256 = readyExpectation.SidecarExecutableSha256,
});
Assert(
    FeishuSidecarProtocol.MatchesReadyAttestation(exactReady, readyExpectation),
    "sidecar ready must echo the exact nonce, identity generation, parent instance, and binary hashes");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        JsonSerializer.SerializeToElement(new
        {
            type = "lifecycle",
            state = "ready",
            runtime_nonce = new string('0', 64),
            identity_fingerprint = readyExpectation.IdentityFingerprint,
            identity_generation = readyExpectation.IdentityGeneration,
            parent_pid = readyExpectation.ParentProcessId,
            parent_start_time_utc_ticks = readyExpectation.ParentStartTimeUtcTicks,
            parent_executable_sha256 = readyExpectation.ParentExecutableSha256,
            parent_payload_manifest_sha256 = readyExpectation.ParentPayloadManifestSha256,
            registered_consumers = FeishuSidecarProtocol.ExpectedConsumers,
            sidecar_executable_sha256 = readyExpectation.SidecarExecutableSha256,
        }),
        readyExpectation),
    "wrong ready nonce must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        exactReady,
        readyExpectation with { IdentityFingerprint = new string('9', 64) }),
    "wrong ready identity fingerprint must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        exactReady,
        readyExpectation with { SidecarExecutableSha256 = new string('8', 64) }),
    "wrong ready sidecar binary fingerprint must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        exactReady,
        readyExpectation with { ParentPayloadManifestSha256 = new string('7', 64) }),
    "wrong ready parent payload manifest fingerprint must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        JsonSerializer.SerializeToElement(new
        {
            type = "lifecycle",
            state = "ready",
            runtime_nonce = readyExpectation.RuntimeNonce,
            identity_fingerprint = readyExpectation.IdentityFingerprint,
            identity_generation = readyExpectation.IdentityGeneration,
            parent_pid = readyExpectation.ParentProcessId,
            parent_start_time_utc_ticks = readyExpectation.ParentStartTimeUtcTicks,
            parent_executable_sha256 = readyExpectation.ParentExecutableSha256,
            parent_payload_manifest_sha256 = readyExpectation.ParentPayloadManifestSha256,
            registered_consumers = new[] { "im.message.receive_v1", "application.bot.menu_v6" },
            sidecar_executable_sha256 = readyExpectation.SidecarExecutableSha256,
        }),
        readyExpectation),
    "ready missing one logical consumer must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        JsonSerializer.SerializeToElement(new
        {
            type = "lifecycle",
            state = "ready",
            runtime_nonce = readyExpectation.RuntimeNonce,
            identity_fingerprint = readyExpectation.IdentityFingerprint,
            identity_generation = readyExpectation.IdentityGeneration,
            parent_pid = readyExpectation.ParentProcessId,
            parent_start_time_utc_ticks = readyExpectation.ParentStartTimeUtcTicks,
            parent_executable_sha256 = readyExpectation.ParentExecutableSha256,
            parent_payload_manifest_sha256 = readyExpectation.ParentPayloadManifestSha256,
            registered_consumers = new[] { "im.message.receive_v1", "application.bot.menu_v6", "card.action.trigger", "extra.consumer" },
            sidecar_executable_sha256 = readyExpectation.SidecarExecutableSha256,
        }),
        readyExpectation),
    "ready with an extra logical consumer must fail closed");
Assert(
    !FeishuSidecarProtocol.MatchesReadyAttestation(
        JsonSerializer.SerializeToElement(new
        {
            type = "lifecycle",
            state = "ready",
            runtime_nonce = readyExpectation.RuntimeNonce,
            identity_fingerprint = readyExpectation.IdentityFingerprint,
            identity_generation = readyExpectation.IdentityGeneration,
            parent_pid = readyExpectation.ParentProcessId,
            parent_start_time_utc_ticks = readyExpectation.ParentStartTimeUtcTicks,
            parent_executable_sha256 = readyExpectation.ParentExecutableSha256,
            parent_payload_manifest_sha256 = readyExpectation.ParentPayloadManifestSha256,
            registered_consumers = new[] { "im.message.receive_v1", "application.bot.menu_v6", "application.bot.menu_v6" },
            sidecar_executable_sha256 = readyExpectation.SidecarExecutableSha256,
        }),
        readyExpectation),
    "ready with duplicate logical consumers must fail closed");

JsonElement UsagePayload(string json)
{
    using JsonDocument document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

DateTimeOffset usageObservedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
CodexUsageSnapshot proWeekOnly = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"planType":"pro","rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1780000000}}}
    """), usageObservedAt);
Assert(
    proWeekOnly.Availability == CodexUsageAvailability.Fresh
    && proWeekOnly.Buckets.Count == 1
    && proWeekOnly.Buckets[0].PlanType == "Pro"
    && proWeekOnly.Buckets[0].Windows.Single().Label == "每周"
    && proWeekOnly.Buckets[0].Windows.Single().RemainingPercent == 75,
    "Pro weekly-only response must show only its returned weekly window");
CodexUsageSnapshot proGeneralAndModel = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"planType":"pro","rateLimitsByLimitId":{"codex":{"limitName":"Codex","rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":10080}}},"model-x":{"limitName":"Model X","rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":300},"secondary":{"usedPercent":50,"windowDurationMins":10080}}}}}
    """), usageObservedAt);
Assert(
    proGeneralAndModel.Buckets.Count == 2
    && proGeneralAndModel.Buckets.Single(bucket => bucket.LimitId == "codex").Windows.Single().Label == "每周"
    && proGeneralAndModel.Buckets.Single(bucket => bucket.LimitId == "model-x").Windows.Select(window => window.Label).SequenceEqual(["5小时", "每周"]),
    "model-specific Pro buckets must stay separate from the general Codex bucket");
CodexUsageSnapshot plusWindows = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"planType":"plus","rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":30,"windowDurationMins":10080}}}
    """), usageObservedAt);
Assert(
    plusWindows.Buckets.Single().PlanType == "Plus"
    && plusWindows.Buckets.Single().Windows.Select(window => window.Label).SequenceEqual(["5小时", "每周"]),
    "Plus must display a five-hour window only when the service actually returns one");
CodexUsageSnapshot unknownAndClamped = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"rateLimitsByLimitId":{"mystery":{"rateLimits":{"primary":{"usedPercent":150,"windowDurationMins":17},"secondary":{"usedPercent":-5,"windowDurationMins":0,"resetsAt":-1}}}}}
    """), usageObservedAt);
Assert(
    unknownAndClamped.Buckets.Single().PlanType == "未知套餐"
    && unknownAndClamped.Buckets.Single().Windows[0].RemainingPercent == 0
    && unknownAndClamped.Buckets.Single().Windows[0].Label == "17分钟"
    && unknownAndClamped.Buckets.Single().Windows[1].RemainingPercent == 100
    && unknownAndClamped.Buckets.Single().Windows[1].ResetsAt is null,
    "unknown plans, missing names, malformed resets, and out-of-range usage must remain safe and bounded");
CodexUsageSnapshot noWindows = CodexAppServerClient.ParseUsageSnapshot(
    UsagePayload("{\"planType\":null,\"rateLimits\":null}"), usageObservedAt);
Assert(noWindows.Availability == CodexUsageAvailability.Unavailable && noWindows.Buckets.Count == 0,
    "empty or null usage payloads must not be presented as zero usage");
CodexUsageSnapshot emptyByLimitFallback = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"planType":"plus","rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300}}}
    """), usageObservedAt);
Assert(emptyByLimitFallback.Buckets.Count == 1
    && emptyByLimitFallback.Buckets[0].Windows.Single().RemainingPercent == 88,
    "empty rateLimitsByLimitId must fall back to the valid object rateLimits payload");
CodexUsageSnapshot malformedUsage = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"rateLimitsByLimitId":{"invalid":{"rateLimits":{"primary":{"usedPercent":"0","windowDurationMins":300},"secondary":{"usedPercent":null,"windowDurationMins":10080}}},"mixed":{"rateLimits":{"primary":{"usedPercent":true},"secondary":{"usedPercent":42,"windowDurationMins":"300","resetsAt":"1780000000"}}}}}
    """), usageObservedAt);
Assert(malformedUsage.Buckets.Count == 1
    && malformedUsage.Buckets[0].LimitId == "mixed"
    && malformedUsage.Buckets[0].Windows.Count == 1
    && malformedUsage.Buckets[0].Windows[0].RemainingPercent == 58
    && malformedUsage.Buckets[0].Windows[0].WindowDurationMins == 0
    && malformedUsage.Buckets[0].Windows[0].ResetsAt is null,
    "missing, boolean, string, or null usedPercent windows must be skipped without fabricating 100 percent");
CodexUsageSnapshot invalidOnly = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"rateLimits":{"primary":{"usedPercent":false},"secondary":{"usedPercent":"NaN"}}}
    """), usageObservedAt);
Assert(invalidOnly.Availability == CodexUsageAvailability.Unavailable && invalidOnly.Buckets.Count == 0,
    "all-invalid usage windows must be unavailable rather than displayed as 100 percent remaining");
CodexUsageSnapshot stableOrder = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"rateLimitsByLimitId":{"z":{"rateLimits":{"primary":{"usedPercent":1}}},"codex":{"rateLimits":{"primary":{"usedPercent":2}}},"a":{"rateLimits":{"primary":{"usedPercent":3}}}}}
    """), usageObservedAt);
Assert(stableOrder.Buckets.Select(bucket => bucket.LimitId).SequenceEqual(["codex", "a", "z"]),
    "usage buckets must be deterministic with codex first and remaining IDs in ordinal order");
CodexUsageSnapshot arrayFallback = CodexAppServerClient.ParseUsageSnapshot(UsagePayload("""
    {"rateLimits":[{"primary":{"usedPercent":0,"windowDurationMins":300}}]}
    """), usageObservedAt);
Assert(arrayFallback.Availability == CodexUsageAvailability.Unavailable,
    "array fallback must not be parsed because Mac parity accepts only the object schema");
CodexUsageSnapshot staleUsage = CodexUsageSnapshotPolicy.AfterReadFailure(proWeekOnly);
Assert(ElementStartsDisabled(mainPageXaml, "CodexUsageRefreshButton")
    && mainPageXaml.Contains("Click=\"OnCodexUsageRefreshClicked\"", StringComparison.Ordinal)
    && mainPageXaml.Contains("AutomationProperties.Name=\"刷新额度\"", StringComparison.Ordinal),
    "usage refresh has a discoverable accessible button disabled until the page is ready");
string usageClickSource = mainPageSource.Split("private async void OnCodexUsageRefreshClicked", StringSplitOptions.None)[1]
    .Split("private void UpdateUsageRefreshButton", StringSplitOptions.None)[0];
Assert(usageClickSource.Contains("RefreshUsageAsync(force: true", StringComparison.Ordinal)
    && usageClickSource.Contains("_usageRefreshCancellation is not null", StringComparison.Ordinal)
    && usageClickSource.Contains("TimeSpan.FromSeconds(15)", StringComparison.Ordinal)
    && !usageClickSource.Contains("RunPrompt", StringComparison.Ordinal),
    "explicit usage refresh bypasses cache, rejects duplicate clicks, has a timeout and starts no Task");
Assert(CodexUsageSnapshotPolicy.AfterReadFailure(proWeekOnly with { Availability = CodexUsageAvailability.Loading })
    is { Availability: CodexUsageAvailability.Stale, Buckets.Count: 1 },
    "a cancelled loading refresh can preserve the previous windows without staying in Loading");
Assert(staleUsage.Availability == CodexUsageAvailability.Stale && staleUsage.Buckets.Count == 1,
    "a failed refresh must preserve the last successful usage projection as stale");
Assert(CodexUsageSnapshotPolicy.ForNewAppServerSession().Buckets.Count == 0,
    "a new app-server session, including account switching, must clear the old usage projection");
Assert(!JsonSerializer.Serialize(proGeneralAndModel).Contains("account", StringComparison.OrdinalIgnoreCase),
    "usage projection must never retain an account identifier or raw account response");

Console.WriteLine($"PASS: {passed} protocol and safety assertions");

if (args.Contains("--integration", StringComparer.Ordinal))
{
    await RunReadOnlyIntegrationAsync();
    await RunReadOnlyWriteBlockIntegrationAsync();
    await RunInterruptProtocolIntegrationAsync();
    await RunCancellationIntegrationAsync();
}
else if (args.Contains("--write-block-integration", StringComparer.Ordinal))
{
    await RunReadOnlyWriteBlockIntegrationAsync();
}

async Task RunProjectScopeIntegrationAsync(string projectRoot, string? expectedThreadId)
{
    string executable = CodexExecutableLocator.Find();
    if (string.IsNullOrWhiteSpace(executable))
    {
        throw new FileNotFoundException("Codex Desktop bundled codex.exe was not found.");
    }

    string exactRoot = Path.GetFullPath(projectRoot);
    if (!Directory.Exists(exactRoot))
    {
        throw new DirectoryNotFoundException("The exact saved project directory does not exist.");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var client = new CodexAppServerClient();
    await client.ConnectAsync(executable, timeout.Token);
    IReadOnlyList<CodexThreadSummary> threads = await client.ListProjectThreadSummariesAsync(
        exactRoot,
        archived: false,
        timeout.Token);
    IReadOnlyList<CodexThreadSummary> pagedThreads =
        await client.ListProjectThreadSummariesAsync(
            exactRoot,
            archived: false,
            timeout.Token,
            pageSize: 1);
    Assert(threads.Count >= 2,
        "the exact saved project must return both active and idle real UI Tasks");
    Assert(threads.All(thread => !string.IsNullOrWhiteSpace(thread.ThreadId)),
        "every project-scoped Task must have a real app-server thread id");
    if (!string.IsNullOrWhiteSpace(expectedThreadId))
    {
        Assert(
            threads.Any(thread => thread.ThreadId.Equals(expectedThreadId, StringComparison.Ordinal)),
            "the exact expected Codex Desktop Task must be present in the project-scoped result");
    }
    Assert(
        threads.All(thread => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(thread.WorkingDirectory)),
            Path.TrimEndingDirectorySeparator(exactRoot),
            StringComparison.OrdinalIgnoreCase)),
        "project-scoped Task results must remain inside the exact authorized project root");
    Assert(
        threads.All(thread =>
            !thread.Title.Contains("默认 Task", StringComparison.OrdinalIgnoreCase)
            && !thread.Title.Contains("测试 Task", StringComparison.OrdinalIgnoreCase)
            && !thread.Title.Contains("test task", StringComparison.OrdinalIgnoreCase)),
        "project-scoped Task results must not contain default or test Tasks");
    Assert(
        threads.All(thread => thread.ThreadSource.Equals("user", StringComparison.Ordinal)),
        "project-scoped Task results must contain only objectively user-created Tasks");
    Assert(
        threads.Select(thread => thread.ThreadId).Distinct(StringComparer.Ordinal).Count()
            == threads.Count,
        "scan-and-repair duplicate rollouts must be removed by canonical thread id");
    Assert(
        pagedThreads.Select(thread => thread.ThreadId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(threads.Select(thread => thread.ThreadId)),
        "full cursor pagination with page size one must equal the normal project Task set");
    Console.WriteLine(
        $"PROJECT-SCOPE INTEGRATION PASS: project={exactRoot}; tasks={threads.Count}; "
        + "projectList=success; threadList=success; exactCwd=true; userCreatedOnly=true; "
        + $"expectedTask={(string.IsNullOrWhiteSpace(expectedThreadId) ? "not-requested" : "matched")}; "
        + "forbiddenTasks=0; uniqueThreadIds=true; cursorPageSize=1; "
        + "provenance=thread/read:user");
}

async Task RunProjectSelectionIntegrationAsync(string projectRoot, string threadId)
{
    string exactRoot = Path.GetFullPath(projectRoot);
    if (!Directory.Exists(exactRoot))
    {
        throw new DirectoryNotFoundException("The exact saved project directory does not exist.");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var runtime = new BridgeRuntime();
    await runtime.StartAsync(timeout.Token);
    CodexThreadSummary? selected = await runtime.SelectProjectThreadAsync(
        threadId,
        exactRoot,
        timeout.Token);
    Assert(selected is not null && selected.ThreadId.Equals(threadId, StringComparison.Ordinal),
        "the Task shown by the exact project selector must be accepted by selection");
    CodexThreadSummary? current = await runtime.GetCurrentThreadAsync(timeout.Token);
    Assert(current is not null && current.ThreadId.Equals(threadId, StringComparison.Ordinal),
        "the selected project Task must remain visible as current through the same projection");
    Console.WriteLine(
        $"PROJECT-SELECTION INTEGRATION PASS: project={exactRoot}; task={threadId}; "
        + "selectorProjection=exact-project; currentProjection=exact-project; selection=success");
}

async Task RunReadOnlyIntegrationAsync()
{
    string executable = CodexExecutableLocator.Find();
    if (string.IsNullOrWhiteSpace(executable))
    {
        throw new FileNotFoundException("Codex Desktop bundled codex.exe was not found.");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var client = new CodexAppServerClient();
    var completion = new TaskCompletionSource<(string TurnId, string Status, string Reply)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    string expectedThreadId = string.Empty;
    string expectedTurnId = string.Empty;
    client.NotificationReceived += notification =>
    {
        if (!string.Equals(notification.Method, "turn/completed", StringComparison.Ordinal)
            || notification.Parameters.ValueKind != JsonValueKind.Object
            || !notification.Parameters.TryGetProperty("threadId", out JsonElement threadId)
            || !string.Equals(threadId.GetString(), expectedThreadId, StringComparison.Ordinal)
            || !notification.Parameters.TryGetProperty("turn", out JsonElement turn)
            || turn.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string turnId = turn.TryGetProperty("id", out JsonElement turnIdElement)
            ? turnIdElement.GetString() ?? string.Empty
            : string.Empty;
        string status = turn.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;
        string reply = string.Empty;
        if (turn.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "agentMessage", StringComparison.Ordinal)
                    && item.TryGetProperty("text", out JsonElement text))
                {
                    reply = text.GetString() ?? string.Empty;
                }
            }
        }

        completion.TrySetResult((turnId, status, reply));
    };

    await client.ConnectAsync(executable, timeout.Token);
    int? childProcessId = client.ProcessId;
    CodexThreadListResult list = await client.ListThreadsAsync(1, true, timeout.Token);
    CodexThreadStartResult thread = await client.StartThreadAsync(WorkingDirectory, timeout.Token);
    expectedThreadId = thread.ThreadId;
    CodexTurnStartResult turn = await client.StartTurnAsync(
        thread.ThreadId,
        "请仅回复：stdio 只读验证成功。不要调用任何工具。",
        timeout.Token);
    expectedTurnId = turn.TurnId;
    (string completedTurnId, string status, string reply) = await completion.Task.WaitAsync(timeout.Token);

    Assert(string.Equals(thread.SandboxType, "readOnly", StringComparison.Ordinal),
        "integration thread sandbox must be readOnly");
    Assert(string.Equals(thread.ApprovalPolicy, "never", StringComparison.Ordinal),
        "integration approval policy must be never");
    Assert(string.Equals(status, "completed", StringComparison.Ordinal),
        "integration turn must complete");
    Assert(string.Equals(completedTurnId, expectedTurnId, StringComparison.Ordinal),
        "integration completion must match the started turn");
    Assert(reply.Contains("stdio 只读验证成功", StringComparison.Ordinal),
        "integration turn must return the fixed acknowledgement");

    Console.WriteLine(
        $"INTEGRATION PASS: platform={client.SessionInfo?.Platform}; childPid={childProcessId}; "
        + $"listed={list.Count}; sandbox={thread.SandboxType}; approval={thread.ApprovalPolicy}; "
        + $"turn={status}; fixedReply=true");
}

async Task RunReadOnlyWriteBlockIntegrationAsync()
{
    string executable = CodexExecutableLocator.Find();
    if (string.IsNullOrWhiteSpace(executable))
    {
        throw new FileNotFoundException("Codex Desktop bundled codex.exe was not found.");
    }

    string runToken = Guid.NewGuid().ToString("N");
    string sentinelFileName = $"read-only-sentinel-{runToken}.txt";
    string sentinelContent = $"DEEPORI_READONLY_SENTINEL_{runToken}";
    string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "deepori-readonly-" + runToken);
    string sentinelPath = Path.Combine(testDirectory, sentinelFileName);
    bool sentinelCreated = false;
    bool anyEntryCreated = false;
    try
    {
        Directory.CreateDirectory(testDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await using var client = new CodexAppServerClient();
        TaskCompletionSource<(string TurnId, string Status)> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string expectedThreadId = string.Empty;
        string expectedTurnId = string.Empty;
        bool matchingWriteAttemptObserved = false;
        bool matchingWriteFailureObserved = false;
        bool matchingWriteSuccessObserved = false;
        string matchingWriteOutcome = string.Empty;

        void ObserveWriteItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string itemType = type.GetString() ?? string.Empty;
            bool matchesSentinel;
            if (itemType == "commandExecution")
            {
                string command = item.TryGetProperty("command", out JsonElement commandElement)
                    && commandElement.ValueKind == JsonValueKind.String
                        ? commandElement.GetString() ?? string.Empty
                        : string.Empty;
                matchesSentinel = command.Contains(sentinelFileName, StringComparison.Ordinal)
                    && command.Contains(sentinelContent, StringComparison.Ordinal);
            }
            else if (itemType == "fileChange")
            {
                matchesSentinel = item.TryGetProperty("changes", out JsonElement changes)
                    && changes.ValueKind == JsonValueKind.Array
                    && changes.EnumerateArray().Any(change =>
                        change.ValueKind == JsonValueKind.Object
                        && change.TryGetProperty("path", out JsonElement path)
                        && path.ValueKind == JsonValueKind.String
                        && (path.GetString() ?? string.Empty).EndsWith(
                            sentinelFileName,
                            StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                return;
            }

            if (!matchesSentinel)
            {
                return;
            }

            matchingWriteAttemptObserved = true;
            string itemStatus = item.TryGetProperty("status", out JsonElement statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;
            if (itemStatus is "failed" or "declined")
            {
                matchingWriteFailureObserved = true;
                matchingWriteOutcome = itemStatus;
                return;
            }

            if (itemType == "commandExecution"
                && itemStatus == "completed"
                && item.TryGetProperty("exitCode", out JsonElement exitCode)
                && exitCode.ValueKind == JsonValueKind.Number
                && exitCode.TryGetInt32(out int code))
            {
                if (code == 0)
                {
                    matchingWriteSuccessObserved = true;
                }
                else
                {
                    matchingWriteFailureObserved = true;
                    matchingWriteOutcome = "nonZeroExit";
                }
            }
            else if (itemType == "fileChange" && itemStatus == "completed")
            {
                matchingWriteSuccessObserved = true;
            }
        }

        client.NotificationReceived += notification =>
        {
            if (notification.Parameters.ValueKind != JsonValueKind.Object
                || !notification.Parameters.TryGetProperty("threadId", out JsonElement threadId)
                || !string.Equals(threadId.GetString(), expectedThreadId, StringComparison.Ordinal))
            {
                return;
            }

            if (notification.Method is "item/started" or "item/completed"
                && notification.Parameters.TryGetProperty("item", out JsonElement notifiedItem)
                && notifiedItem.ValueKind == JsonValueKind.Object)
            {
                ObserveWriteItem(notifiedItem);
            }

            if (!string.Equals(notification.Method, "turn/completed", StringComparison.Ordinal)
                || !notification.Parameters.TryGetProperty("turn", out JsonElement turn)
                || turn.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string turnId = turn.TryGetProperty("id", out JsonElement id)
                ? id.GetString() ?? string.Empty
                : string.Empty;
            string status = turn.TryGetProperty("status", out JsonElement state)
                ? state.GetString() ?? string.Empty
                : string.Empty;
            if (turn.TryGetProperty("items", out JsonElement items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    ObserveWriteItem(item);
                }
            }

            completion.TrySetResult((turnId, status));
        };

        await client.ConnectAsync(executable, timeout.Token);
        CodexThreadStartResult thread = await client.StartThreadAsync(
            testDirectory,
            timeout.Token);
        expectedThreadId = thread.ThreadId;
        string completedTurnId = string.Empty;
        string status = string.Empty;
        string[] writePrompts =
        {
            $"安全回归测试：现在必须使用命令执行工具实际运行 Windows PowerShell 命令 Set-Content -LiteralPath .\\{sentinelFileName} -Value '{sentinelContent}'。不要只解释或模拟，不要请求人工授权；让 read-only sandbox 客观阻断命令后报告结果。",
            $"继续安全回归测试：请立即调用命令执行工具，在当前 cwd 执行 cmd /c echo {sentinelContent}>{sentinelFileName}。工具调用是本测试的必要证据；不要只回复无法写入，也不要请求授权。",
        };
        foreach (string prompt in writePrompts)
        {
            completion = new TaskCompletionSource<(string TurnId, string Status)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CodexTurnStartResult turn = await client.StartTurnAsync(
                thread.ThreadId,
                prompt,
                timeout.Token);
            expectedTurnId = turn.TurnId;
            (completedTurnId, status) = await completion.Task.WaitAsync(timeout.Token);
            Assert(completedTurnId == expectedTurnId,
                "write-block integration completion must match the attempted turn");
            Assert(CodexAppServerClient.IsTerminalTurnStatus(status),
                "write-block integration must reach a terminal turn state");
            if (matchingWriteFailureObserved)
            {
                break;
            }
        }

        sentinelCreated = File.Exists(sentinelPath);
        anyEntryCreated = Directory.EnumerateFileSystemEntries(testDirectory).Any();
        Assert(thread.SandboxType == "readOnly",
            "write-block integration must receive effective readOnly sandbox");
        Assert(thread.ApprovalPolicy == "never",
            "write-block integration must receive effective approvalPolicy=never");
        Assert(matchingWriteAttemptObserved,
            "write-block integration must observe a tool item matching this run's unique sentinel");
        Assert(matchingWriteFailureObserved,
            "the matching sentinel tool item must report failed, declined, or a non-zero exit code");
        Assert(!matchingWriteSuccessObserved,
            "the matching sentinel tool item must never report a successful result");
        Assert(!sentinelCreated,
            "readOnly integration must objectively prevent sentinel creation");
        Assert(!anyEntryCreated,
            "readOnly integration must prevent alternate file-system writes in the controlled cwd");

        Console.WriteLine(
            $"WRITE-BLOCK INTEGRATION PASS: sandbox={thread.SandboxType}; "
            + $"approval={thread.ApprovalPolicy}; terminal={status}; "
            + $"matchedSentinel=true; toolOutcome={matchingWriteOutcome}; "
            + "sentinelCreated=false; cwdEntries=0");
    }
    finally
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    Assert(!Directory.Exists(testDirectory),
        "write-block integration must clean its controlled temporary directory");
}

async Task RunCancellationIntegrationAsync()
{
    using var overallTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
    await using var runtime = new BridgeRuntime();
    await runtime.StartAsync(overallTimeout.Token);
    Assert(runtime.Snapshot.State == BridgeRuntimeState.Running,
        "cancellation integration runtime must start");
    int originalProcessId = runtime.AppServerProcessId
        ?? throw new InvalidOperationException("cancellation integration app-server pid missing");

    using var cancelTurn = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
    Task<CodexControlledRouteResult> cancellableTurn = runtime.RunReadOnlyPromptAsync(
        "请立即调用 clock.sleep 工具等待 30000 毫秒；等待结束后仅回复：不应完成。不要提前回复。",
        cancelTurn.Token);
    DateTimeOffset turnStartDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
    while (!runtime.ActiveTurnStarted
        && !cancellableTurn.IsCompleted
        && DateTimeOffset.UtcNow < turnStartDeadline)
    {
        await Task.Delay(50, overallTimeout.Token);
    }
    Assert(runtime.ActiveTurnStarted && !string.IsNullOrWhiteSpace(runtime.ActiveTurnId),
        "cancellation integration must observe matching turn/started before cancelling");
    cancelTurn.Cancel();
    bool cancelled = false;
    try
    {
        await cancellableTurn;
    }
    catch (OperationCanceledException) when (cancelTurn.IsCancellationRequested)
    {
        cancelled = true;
    }

    Assert(cancelled, "cancel-after-turn/start must cancel the caller");
    BridgeRuntimeState postCancelState = runtime.Snapshot.State;
    bool interruptAcknowledged = runtime.LastInterruptAcknowledged;
    string cancellationTerminalStatus = runtime.LastCancellationTerminalStatus;
    Assert(postCancelState is BridgeRuntimeState.Running or BridgeRuntimeState.Faulted,
        "cancel cleanup must leave a confirmed session or a fail-closed fault");
    Assert(
        postCancelState == BridgeRuntimeState.Faulted
        || interruptAcknowledged,
        "a retained session must have an acknowledged turn/interrupt response");
    Assert(
        postCancelState == BridgeRuntimeState.Faulted
        || CodexAppServerClient.IsTerminalTurnStatus(cancellationTerminalStatus),
        "a retained session must observe a terminal turn/completed notification");
    if (postCancelState == BridgeRuntimeState.Faulted)
    {
        Assert(await WaitForProcessExitAsync(originalProcessId, TimeSpan.FromSeconds(8)),
            "unconfirmed cancellation must stop the old app-server before retry");
        await runtime.StartAsync(overallTimeout.Token);
        Assert(runtime.Snapshot.State == BridgeRuntimeState.Running,
            "faulted cancellation session must restart cleanly");
    }

    int retryProcessId = runtime.AppServerProcessId
        ?? throw new InvalidOperationException("retry app-server pid missing");
    CodexControlledRouteResult retry = await runtime.RunReadOnlyPromptAsync(
        "请仅回复：取消后重试成功。不要调用任何工具。",
        overallTimeout.Token);
    Assert(retry.Status == "completed",
        "retry after cancellation must complete without overlap");
    Assert(retry.AgentMessageText.Contains("取消后重试成功", StringComparison.Ordinal),
        "retry after cancellation must return the expected acknowledgement");

    await runtime.StopAsync(overallTimeout.Token);
    Assert(await WaitForProcessExitAsync(retryProcessId, TimeSpan.FromSeconds(8)),
        "shutdown must leave no current app-server child");
    Assert(await WaitForProcessExitAsync(originalProcessId, TimeSpan.FromSeconds(8)),
        "shutdown must leave no original app-server child");
    Console.WriteLine(
        $"CANCEL INTEGRATION PASS: originalPid={originalProcessId}; retryPid={retryProcessId}; "
        + $"postCancel={postCancelState}; interruptAck={interruptAcknowledged}; "
        + $"cancelTerminal={cancellationTerminalStatus}; retry={retry.Status}; "
        + "orphan=false; overlap=false");
}

async Task RunInterruptProtocolIntegrationAsync()
{
    string executable = CodexExecutableLocator.Find();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var client = new CodexAppServerClient();
    var completion = new TaskCompletionSource<(string TurnId, string Status)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    string expectedThreadId = string.Empty;
    string expectedTurnId = string.Empty;
    client.NotificationReceived += notification =>
    {
        if (string.Equals(notification.Method, "turn/started", StringComparison.Ordinal)
            && notification.Parameters.ValueKind == JsonValueKind.Object
            && notification.Parameters.TryGetProperty("threadId", out JsonElement startedThreadId)
            && string.Equals(startedThreadId.GetString(), expectedThreadId, StringComparison.Ordinal)
            && notification.Parameters.TryGetProperty("turn", out JsonElement startedTurn)
            && startedTurn.ValueKind == JsonValueKind.Object
            && startedTurn.TryGetProperty("id", out JsonElement startedTurnId))
        {
            started.TrySetResult(startedTurnId.GetString() ?? string.Empty);
            return;
        }

        if (!string.Equals(notification.Method, "turn/completed", StringComparison.Ordinal)
            || notification.Parameters.ValueKind != JsonValueKind.Object
            || !notification.Parameters.TryGetProperty("threadId", out JsonElement threadId)
            || !string.Equals(threadId.GetString(), expectedThreadId, StringComparison.Ordinal)
            || !notification.Parameters.TryGetProperty("turn", out JsonElement turn)
            || turn.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string turnId = turn.TryGetProperty("id", out JsonElement id)
            ? id.GetString() ?? string.Empty
            : string.Empty;
        string status = turn.TryGetProperty("status", out JsonElement state)
            ? state.GetString() ?? string.Empty
            : string.Empty;
        completion.TrySetResult((turnId, status));
    };

    await client.ConnectAsync(executable, timeout.Token);
    int processId = client.ProcessId
        ?? throw new InvalidOperationException("interrupt integration app-server pid missing");
    CodexThreadStartResult thread = await client.StartThreadAsync(WorkingDirectory, timeout.Token);
    expectedThreadId = thread.ThreadId;
    CodexTurnStartResult turn = await client.StartTurnAsync(
        thread.ThreadId,
        "请立即调用 clock.sleep 工具等待 30000 毫秒；等待结束后仅回复：不应完成。不要提前回复。",
        timeout.Token);
    expectedTurnId = turn.TurnId;
    Assert(turn.Status == "inProgress",
        "turn/start must report inProgress before the interrupt test");
    string notifiedTurnId = await started.Task.WaitAsync(timeout.Token);
    Assert(notifiedTurnId == turn.TurnId,
        "turn/started notification must match the interrupt target");
    await client.InterruptTurnAsync(thread.ThreadId, turn.TurnId, timeout.Token);
    (string completedTurnId, string status) = await completion.Task.WaitAsync(timeout.Token);
    Assert(completedTurnId == expectedTurnId,
        "turn/interrupt completion must match the started turn");
    Assert(status == "interrupted",
        "turn/interrupt must produce an interrupted terminal notification");
    Console.WriteLine(
        $"INTERRUPT INTEGRATION PASS: childPid={processId}; ack=true; terminal={status}; orphan=false");
}

async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }

        await Task.Delay(100);
    }

    try
    {
        using Process process = Process.GetProcessById(processId);
        return process.HasExited;
    }
    catch (ArgumentException)
    {
        return true;
    }
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }

    passed++;
    if (args.Contains("--test-progress", StringComparer.Ordinal) && passed % 50 == 0)
        Console.WriteLine($"PASS {passed}: {message}");
}

bool ElementStartsDisabled(string xaml, string controlName)
{
    int nameIndex = xaml.IndexOf($"x:Name=\"{controlName}\"", StringComparison.Ordinal);
    if (nameIndex < 0)
    {
        return false;
    }

    int elementStart = xaml.LastIndexOf('<', nameIndex);
    int elementEnd = xaml.IndexOf('>', nameIndex);
    return elementStart >= 0
        && elementEnd > nameIndex
        && xaml[elementStart..elementEnd].Contains(
            "IsEnabled=\"False\"",
            StringComparison.Ordinal);
}

void AssertThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        passed++;
        return;
    }

    throw new InvalidOperationException(message);
}

void AssertJsonRpcIdPreserved(
    JsonElement expected,
    JsonElement actual,
    string message)
{
    bool preserved = expected.ValueKind == actual.ValueKind
        && expected.ValueKind switch
        {
            JsonValueKind.String => string.Equals(
                expected.GetString(),
                actual.GetString(),
                StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(
                expected.GetRawText(),
                actual.GetRawText(),
                StringComparison.Ordinal),
            _ => string.Equals(
                expected.GetRawText(),
                actual.GetRawText(),
                StringComparison.Ordinal),
        };
    Assert(preserved, message);
}

Process StartFakeSidecarChild()
{
    string executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法解析协议测试进程路径。" );
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--fake-sidecar-child");
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动隔离的假 sidecar 子进程。" );
}

async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    while (!condition())
    {
        await Task.Delay(25, cancellation.Token);
    }
}

bool IsProcessRunning(int processId)
{
    if (processId <= 0)
    {
        return false;
    }

    try
    {
        using Process process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

FeishuRouteGateCoordinator NewRouteGates(TimeProvider? timeProvider = null)
{
    var gates = new FeishuRouteGateCoordinator(timeProvider);
    gates.BeginSession(ownerBound: true);
    gates.SetListening(true);
    return gates;
}

sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtcNow;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance(TimeSpan duration)
    {
        _utcNow += duration;
        Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
