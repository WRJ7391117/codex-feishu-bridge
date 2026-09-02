using System.Diagnostics;
using System.Text.Json;
using DeepOriBridge.Models;
using DeepOriBridge.Services;

const string WorkingDirectory = @"D:\Evolution\Codex-Feishu-Bridge";
int passed = 0;

Assert(
    BridgeProductIdentity.IsExpectedCandidate(
        @"D:\Bridge\DeepOriBridge.exe",
        BridgeProductIdentity.ExpectedProductName,
        BridgeProductIdentity.ExpectedPackageName),
    "exact Bridge executable, product, and package identity must pass");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        @"C:\Program Files\WindowsApps\DeepOri.AgentMesh\DeepOriAgentMesh.exe",
        "DeepOri Agent Mesh",
        BridgeProductIdentity.ForbiddenMeshPackageName),
    "Mesh must never be accepted as Bridge");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        @"D:\Bridge\DeepOriBridge.exe",
        BridgeProductIdentity.ExpectedProductName,
        BridgeProductIdentity.ForbiddenMeshPackageName),
    "Bridge executable under Mesh package identity must fail closed");
Assert(
    !BridgeProductIdentity.IsExpectedCandidate(
        @"D:\Bridge\DeepOriBridge.exe",
        "DeepOri Agent Mesh",
        BridgeProductIdentity.ExpectedPackageName),
    "product metadata mismatch must fail closed");
string[] reversedCandidates =
[
    @"C:\Program Files\WindowsApps\DeepOri.AgentMesh\DeepOriAgentMesh.exe",
    @"D:\Bridge\DeepOriBridge.exe",
];
Assert(
    reversedCandidates.Count(path => BridgeProductIdentity.IsExpectedCandidate(
        path,
        Path.GetFileName(path).Equals("DeepOriBridge.exe", StringComparison.OrdinalIgnoreCase)
            ? BridgeProductIdentity.ExpectedProductName
            : "DeepOri Agent Mesh",
        Path.GetFileName(path).Equals("DeepOriBridge.exe", StringComparison.OrdinalIgnoreCase)
            ? BridgeProductIdentity.ExpectedPackageName
            : BridgeProductIdentity.ForbiddenMeshPackageName)) == 1,
    "candidate ordering must not change exact Bridge selection");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate([]) is null,
    "zero Bridge candidates must fail closed before any action");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate(
    [
        (@"D:\BridgeA\DeepOriBridge.exe", BridgeProductIdentity.ExpectedProductName, BridgeProductIdentity.ExpectedPackageName),
        (@"D:\BridgeB\DeepOriBridge.exe", BridgeProductIdentity.ExpectedProductName, BridgeProductIdentity.ExpectedPackageName),
    ]) is null,
    "multiple exact Bridge candidates must fail closed before any action");
Assert(
    BridgeProductIdentity.SelectUniqueCandidate(
    [
        (@"C:\Mesh\DeepOriAgentMesh.exe", "DeepOri Agent Mesh", BridgeProductIdentity.ForbiddenMeshPackageName),
    ]) is null,
    "Mesh-only installation must fail closed before any action");
string botFingerprint = FeishuAppIdentityPolicy.Fingerprint("cli_bridge_bot");
Assert(
    FeishuAppIdentityPolicy.MayStartSidecar("cli_bridge_bot", true, true, botFingerprint),
    "matching verified app fingerprint may start sidecar");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar("cli_mesh_bot", true, true, botFingerprint),
    "cross-app credential fingerprint must fail closed");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar("cli_bridge_bot", true, false, botFingerprint),
    "legacy credential without verified identity must fail closed");
Assert(
    !FeishuAppIdentityPolicy.MayStartSidecar("cli_bridge_bot", false, true, botFingerprint),
    "verified identity without a secret must fail closed");
string ownerCode = FeishuOwnerBindingCodePolicy.Generate();
DateTimeOffset ownerCodeNow = DateTimeOffset.UtcNow;
Assert(ownerCode.Length == 6 && ownerCode.All(char.IsDigit), "owner binding code must be six digits");
Assert(
    FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), ownerCode, ownerCodeNow),
    "exact unexpired owner binding code must match");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow, ownerCode, ownerCodeNow),
    "expired owner binding code must fail closed");
Assert(
    !FeishuOwnerBindingCodePolicy.Matches(ownerCode, ownerCodeNow.AddMinutes(2), "Windows Bridge 绑定所有者", ownerCodeNow),
    "legacy static owner binding phrase must fail closed");

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
    FeishuEventSafetyPolicy.MatchesFixedCommand(
        "WindowsCodex\u200B路由测试",
        "Windows Codex 路由测试"),
    "fixed commands must ignore missing whitespace and zero-width format characters");
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
    !FeishuEventSafetyPolicy.CanEnterControlledRouteGate(
        "reply-test", approvedTarget: true, ownerMatches: true),
    "other fixed actions must not be diverted into the controlled route gate");

DateTimeOffset gateNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
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
        out bool sidecarSucceeded)
    && parsedRequestId == sidecarRequestId
    && sidecarSucceeded,
    "sidecar command result must contain only opaque correlation and status");
Assert(
    !sidecarResultDocument.RootElement.GetRawText().Contains(syntheticMessageReference, StringComparison.Ordinal)
    && !sidecarResultDocument.RootElement.GetRawText().Contains(syntheticReplyBody, StringComparison.Ordinal),
    "sidecar stdout command result must not echo message references or reply bodies");

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
