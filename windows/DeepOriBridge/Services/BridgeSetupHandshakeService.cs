using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeepOriBridge.Services;

internal sealed record BridgeSetupChallenge(
    string PipeName,
    string Nonce,
    DateTimeOffset ExpiresAt,
    int BridgeProcessId,
    long BridgeProcessStartTimeUtcTicks,
    string BridgeExecutableSha256,
    string BridgePayloadManifestSha256,
    long BridgeWindowHandle,
    string ExpectedPageAutomationId,
    string BridgeExecutablePath,
    string BridgePackageInstallLocation,
    string PackagingMode,
    string BuildChannel,
    bool PackagePathAttested,
    bool DeveloperPathAttested,
    bool LocalPreviewSensitiveRuntimeEnabled,
    bool ProductionCredentialReuseEnabled,
    bool AgentMeshCredentialAccessEnabled,
    string CredentialLockerResourcePrefix,
    bool CredentialLockerIsolated,
    IReadOnlyList<string> PayloadManifestFiles);

internal sealed record BridgeSetupHandshakeRequest(
    string Nonce,
    string Operation,
    int ClientProcessId,
    long ClientProcessStartTimeUtcTicks,
    string ClientExecutablePath,
    string ClientExecutableSha256,
    string ExpectedBridgePayloadManifestSha256);

internal sealed record BridgeSetupHandshakeResponse(
    bool Accepted,
    string Nonce,
    int BridgeProcessId,
    long BridgeProcessStartTimeUtcTicks,
    string BridgeExecutableSha256,
    string BridgePayloadManifestSha256,
    long BridgeWindowHandle,
    string ExpectedPageAutomationId,
    string BridgeExecutablePath,
    string BridgePackageInstallLocation,
    string PackagingMode,
    string BuildChannel,
    bool PackagePathAttested,
    bool DeveloperPathAttested,
    bool LocalPreviewSensitiveRuntimeEnabled,
    bool ProductionCredentialReuseEnabled,
    bool AgentMeshCredentialAccessEnabled,
    string CredentialLockerResourcePrefix,
    bool CredentialLockerIsolated,
    IReadOnlyList<string> PayloadManifestFiles,
    string Message,
    string CommandPipeName = "",
    string CommandSessionToken = "");

internal sealed record BridgeSetupCommandRequest(
    string Nonce, string SessionToken, string Operation, string RequestId,
    int ClientProcessId, long ClientProcessStartTimeUtcTicks,
    string ClientExecutablePath, string ClientExecutableSha256,
    string ExpectedBridgePayloadManifestSha256);

internal sealed record BridgeSetupClientAttestation(
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string ExecutablePath,
    string ExecutableSha256,
    bool HasReparsePoint,
    bool HasExited);

internal sealed record BridgeSetupCommandResponse(
    bool Accepted, string RequestId, string State, string Message);

internal sealed class BridgeSetupCommandReplayGuard
{
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public bool TryConsume(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)
            || requestId.Length != 32
            || !requestId.All(Uri.IsHexDigit))
        {
            return false;
        }
        return _consumed.Add(requestId);
    }

    public void Clear() => _consumed.Clear();
}

internal sealed class BridgeSetupHandshakeService : IAsyncDisposable
{
    internal const int ChallengeLifetimeSeconds = 120;
    internal static readonly TimeSpan AcceptedSessionLifetime = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ChallengeLifetime =
        TimeSpan.FromSeconds(ChallengeLifetimeSeconds);
    internal static string ChallengeLifetimeLabel => $"{ChallengeLifetimeSeconds} 秒";
    internal const string VerifyOnlyOperation = "verify-identity-readonly";
    internal const bool CodexAssistedMutationEnabled = true;
    internal const int MaxCommandJsonBytes = 16 * 1024;

    private readonly BridgeProductIdentity _identity;
    private readonly FeishuCredentialService _credentials;
    private readonly object _lock = new();
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeListener;
    private Task? _activeCommandListener;
    private BridgeSetupChallenge? _acceptedChallenge;
    private BridgeSetupHandshakeRequest? _acceptedCaller;
    private string _commandSessionToken = "";
    private readonly BridgeSetupCommandReplayGuard _commandReplayGuard = new();

    internal event Action? HandshakeAccepted;
    internal event Action<string>? HandshakeUnavailable;
    internal Func<BridgeSetupCommandRequest, CancellationToken, Task<BridgeSetupCommandResponse>>? CommandHandler { get; set; }

    public BridgeSetupHandshakeService(
        BridgeProductIdentity identity,
        FeishuCredentialService credentials)
    {
        _identity = identity;
        _credentials = credentials;
    }

    internal BridgeSetupChallenge IssueChallenge()
    {
        lock (_lock)
        {
            if (_acceptedChallenge is not null)
            {
                throw new InvalidOperationException("当前自动配置会话已完成身份握手；请继续既有会话或明确退出后重启。");
            }
        }
        BridgeCandidateAttestation instance = _identity.GetBoundInstance();
        var challenge = new BridgeSetupChallenge(
            $"DeepOriBridge.Setup.{instance.ProcessId}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow.Add(ChallengeLifetime),
            instance.ProcessId,
            instance.ProcessStartTimeUtcTicks,
            instance.ExecutableSha256,
            instance.PayloadManifestSha256,
            instance.WindowHandle,
            instance.PageAutomationId,
            instance.ExecutablePath,
            Path.GetDirectoryName(instance.ExecutablePath)!,
            instance.PackagingMode,
            _identity.IsPortableIdentity ? PortablePreview.Channel : "unsigned-local-preview",
            instance.PackagePathAttested,
            instance.DeveloperPathAttested,
            _identity.IsLocalPreviewRuntimeEnabled,
            ProductionCredentialReuseEnabled: false,
            AgentMeshCredentialAccessEnabled: false,
            CredentialLockerResourcePrefix: _credentials.ResourcePrefix,
            CredentialLockerIsolated: _credentials.UsesDevelopmentIsolatedNamespace
                || (_identity.IsPortableIdentity && _credentials.ResourcePrefix == PortablePreview.CredentialPrefix),
            PayloadManifestFiles: _identity.PayloadFiles);
        var cancellation = new CancellationTokenSource(ChallengeLifetime);
        lock (_lock)
        {
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = cancellation;
            _activeListener = ListenOnceAsync(challenge, cancellation.Token);
        }

        return challenge;
    }

    internal static string BuildPromptSuffix(BridgeSetupChallenge challenge) => $"""

        本轮只读身份握手（{ChallengeLifetimeLabel}、单次使用；过期或重放必须停止）：
        - pipe: {challenge.PipeName}
        - nonce: {challenge.Nonce}
        - Bridge PID: {challenge.BridgeProcessId}
        - Bridge start UTC ticks: {challenge.BridgeProcessStartTimeUtcTicks}
        - Bridge exe SHA-256: {challenge.BridgeExecutableSha256}
        - Bridge payload manifest SHA-256: {challenge.BridgePayloadManifestSha256}
        - Bridge HWND: 0x{challenge.BridgeWindowHandle:X}
        - expected UIA page: {challenge.ExpectedPageAutomationId}
        - Bridge executable path: {challenge.BridgeExecutablePath}
        - Bridge package install location: {challenge.BridgePackageInstallLocation}
        - packaging mode: {challenge.PackagingMode}
        - build channel: {challenge.BuildChannel}
        - package path attested: {challenge.PackagePathAttested.ToString().ToLowerInvariant()}
        - developer path attested: {challenge.DeveloperPathAttested.ToString().ToLowerInvariant()}
        - local preview sensitive runtime enabled: {challenge.LocalPreviewSensitiveRuntimeEnabled.ToString().ToLowerInvariant()}
        - production credential reuse enabled: {challenge.ProductionCredentialReuseEnabled.ToString().ToLowerInvariant()}
        - Agent Mesh credential access enabled: {challenge.AgentMeshCredentialAccessEnabled.ToString().ToLowerInvariant()}
        - Credential Locker resource prefix: {challenge.CredentialLockerResourcePrefix}
        - Credential Locker isolated: {challenge.CredentialLockerIsolated.ToString().ToLowerInvariant()}
        - payload manifest files: {string.Join(" | ", challenge.PayloadManifestFiles)}

        只能通过当前用户专用 named pipe 执行 verify-identity-readonly。握手不会直接修改配置；通过后使用同一持久 PowerShell 进程中的受限命令通道，配合同一 PID/HWND/精确页面的人工确认入口继续。120 秒仅用于初次握手，接受后的会话最长 30 分钟；进程、页面或身份改变仍立即失效。不得搜索或操作 DeepOri Agent Mesh。
        """;

    private async Task ListenOnceAsync(
        BridgeSetupChallenge challenge,
        CancellationToken cancellationToken)
    {
        int consumed = 0;
        await using var server = new NamedPipeServerStream(
            challenge.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await server.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(
                server,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            string? requestJson = await reader.ReadLineAsync(cancellationToken);
            int actualClientProcessId = GetNamedPipeClientProcessId(
                server.SafePipeHandle,
                out uint clientProcessId)
                    ? checked((int)clientProcessId)
                    : 0;
            BridgeSetupHandshakeResponse response = ValidateRequest(
                challenge,
                requestJson,
                DateTimeOffset.UtcNow,
                () => _identity.GetBoundInstance(),
                () => Interlocked.Exchange(ref consumed, 1) == 0,
                actualClientProcessId);
            if (response.Accepted
                && JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(requestJson!) is { } caller)
            {
                response = BeginCommandSession(challenge, caller, response, cancellationToken);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
            if (!response.Accepted)
            {
                HandshakeUnavailable?.Invoke("身份握手被拒绝；可生成一个新的单次挑战后重试。");
            }
        }
        catch (OperationCanceledException)
        {
            if (DateTimeOffset.UtcNow >= challenge.ExpiresAt)
            {
                HandshakeUnavailable?.Invoke("身份握手已过期；可生成一个新的单次挑战后重试。");
            }
        }
        catch (IOException)
        {
        }
    }

    private BridgeSetupHandshakeResponse BeginCommandSession(
        BridgeSetupChallenge challenge,
        BridgeSetupHandshakeRequest caller,
        BridgeSetupHandshakeResponse response,
        CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
        {
            _acceptedChallenge = challenge;
            _acceptedCaller = caller;
            _commandSessionToken = token;
            _commandReplayGuard.Clear();
            // The short challenge deadline must not terminate an accepted setup.
            _activeCancellation?.CancelAfter(AcceptedSessionLifetime);
            _activeCommandListener = ListenCommandsAsync(challenge, caller, token, cancellationToken);
        }
        HandshakeAccepted?.Invoke();
        return response with { CommandPipeName = challenge.PipeName + ".actions", CommandSessionToken = token };
    }

    private async Task ListenCommandsAsync(
        BridgeSetupChallenge challenge,
        BridgeSetupHandshakeRequest caller,
        string token,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                challenge.PipeName + ".actions", PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, false, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? json = await reader.ReadLineAsync(cancellationToken);
                int actual = GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientId) ? checked((int)clientId) : 0;
                if (!TryParseCommandRequest(json, out BridgeSetupCommandRequest? command))
                {
                    RevokeActiveChallenge();
                    return;
                }
                BridgeSetupCommandResponse response = await DispatchCommandAsync(challenge, caller, token, command, actual, cancellationToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                bool expired;
                lock (_lock) { expired = _acceptedChallenge == challenge; }
                if (expired)
                {
                    RevokeActiveChallenge();
                    HandshakeUnavailable?.Invoke("自动配置会话已到 30 分钟上限；已停止接收操作。请重新授权生成新指令。");
                }
                return;
            }
            catch (IOException) { }
            catch (JsonException) { RevokeActiveChallenge(); return; }
        }
    }

    private async Task<BridgeSetupCommandResponse> DispatchCommandAsync(
        BridgeSetupChallenge challenge, BridgeSetupHandshakeRequest caller, string token,
        BridgeSetupCommandRequest? command, int actualClientProcessId, CancellationToken cancellationToken)
    {
        string requestId = command?.RequestId ?? string.Empty;
        if (!IsAuthorizedCommand(
                challenge, caller, token, command, actualClientProcessId,
                () => _identity.GetBoundInstance()))
        {
            // A failed command re-attestation invalidates the short-lived capability.
            RevokeActiveChallenge();
            return new(false, requestId, "denied", "当前会话身份已失效。");
        }
        lock (_lock)
        {
            if (!_commandReplayGuard.TryConsume(requestId))
            {
                RevokeActiveChallenge();
                return new(false, requestId, "denied", "自动配置请求已重放或格式无效。");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Func<BridgeSetupCommandRequest, CancellationToken, Task<BridgeSetupCommandResponse>>? handler = CommandHandler;
        if (handler is null) return new(false, requestId, "denied", "自动配置页面未就绪。");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<BridgeSetupCommandResponse> pending = handler(command!, operation.Token);
        while (!pending.IsCompleted)
        {
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1), operation.Token));
            if (operation.IsCancellationRequested || !IsAuthorizedCommand(challenge, caller, token, command,
                    actualClientProcessId, () => _identity.GetBoundInstance()))
            {
                operation.Cancel();
                RevokeActiveChallenge();
                try { await pending; } catch (OperationCanceledException) { }
                return new(false, requestId, "denied", "自动配置授权已结束，已请求取消在途操作；远端已完成的变更不会回滚。");
            }
        }
        return await pending;
    }

    internal static bool IsAuthorizedCommand(
        BridgeSetupChallenge challenge,
        BridgeSetupHandshakeRequest caller,
        string token,
        BridgeSetupCommandRequest? command,
        int actualClientProcessId,
        Func<BridgeCandidateAttestation> observeBridge,
        Func<int, BridgeSetupClientAttestation?>? observeCaller = null)
    {
        if (command is null
            || command.RequestId is null
            || command.RequestId.Length != 32
            || command.Nonce is null
            || command.SessionToken is null
            || command.ClientExecutablePath is null
            || command.ClientExecutableSha256 is null
            || command.ExpectedBridgePayloadManifestSha256 is null
            || !FileAttestation.FixedTimeEquals(command.Nonce, challenge.Nonce)
            || !FileAttestation.FixedTimeEquals(command.SessionToken, token)
            || command.ClientProcessId != caller.ClientProcessId
            || actualClientProcessId != caller.ClientProcessId
            || command.ClientProcessStartTimeUtcTicks != caller.ClientProcessStartTimeUtcTicks
            || !string.Equals(command.ClientExecutablePath, caller.ClientExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !FileAttestation.FixedTimeEquals(command.ClientExecutableSha256, caller.ClientExecutableSha256)
            || !FileAttestation.FixedTimeEquals(command.ExpectedBridgePayloadManifestSha256, challenge.BridgePayloadManifestSha256)
            || command.Operation is not ("setup_status" or "remote_menu_status" or "menu_inspect_apply" or "menu_publish" or "feishu_listen" or "owner_gate" or "project_gate" or "roundtrip_status"))
        {
            return false;
        }

        try
        {
            BridgeSetupClientAttestation? liveCaller = (observeCaller ?? ObserveLiveCaller)(
                actualClientProcessId);
            return MatchesLiveCaller(caller, actualClientProcessId, liveCaller)
                && MatchesLiveBridge(challenge, observeBridge());
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseCommandRequest(
        string? json,
        out BridgeSetupCommandRequest? command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)
                || Encoding.UTF8.GetByteCount(json) > MaxCommandJsonBytes)
            {
                command = null;
                return false;
            }
            command = JsonSerializer.Deserialize<BridgeSetupCommandRequest>(json ?? "");
            return command is not null;
        }
        catch (JsonException)
        {
            command = null;
            return false;
        }
    }

    private static BridgeSetupClientAttestation? ObserveLiveCaller(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }
            string executablePath = FileAttestation.CanonicalPath(
                process.MainModule?.FileName ?? string.Empty);
            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            string executableSha256 = FileAttestation.Sha256(executablePath);
            bool reparsePoint = FileAttestation.HasReparsePoint(executablePath);
            return new BridgeSetupClientAttestation(
                process.Id,
                startTicks,
                executablePath,
                executableSha256,
                reparsePoint,
                process.HasExited);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesLiveCaller(
        BridgeSetupHandshakeRequest caller,
        int actualClientProcessId,
        BridgeSetupClientAttestation? liveCaller) =>
        liveCaller is not null
        && !liveCaller.HasExited
        && !liveCaller.HasReparsePoint
        && liveCaller.ProcessId == actualClientProcessId
        && liveCaller.ProcessId == caller.ClientProcessId
        && liveCaller.ProcessStartTimeUtcTicks == caller.ClientProcessStartTimeUtcTicks
        && string.Equals(
            liveCaller.ExecutablePath,
            caller.ClientExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && FileAttestation.FixedTimeEquals(
            liveCaller.ExecutableSha256,
            caller.ClientExecutableSha256);

    internal static Task ListenOnceForVerificationAsync(
        BridgeSetupChallenge challenge,
        CancellationToken cancellationToken,
        Func<BridgeCandidateAttestation> observeBridge) =>
        ListenOnceForVerificationCoreAsync(
            challenge,
            cancellationToken,
            observeBridge);

    private static async Task ListenOnceForVerificationCoreAsync(
        BridgeSetupChallenge challenge,
        CancellationToken cancellationToken,
        Func<BridgeCandidateAttestation> observeBridge)
    {
        int consumed = 0;
        await using var server = new NamedPipeServerStream(
            challenge.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await server.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(
                server,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            string? requestJson = await reader.ReadLineAsync(cancellationToken);
            int actualClientProcessId = GetNamedPipeClientProcessId(
                server.SafePipeHandle,
                out uint clientProcessId)
                    ? checked((int)clientProcessId)
                    : 0;
            BridgeSetupHandshakeResponse response = ValidateRequest(
                challenge,
                requestJson,
                DateTimeOffset.UtcNow,
                observeBridge,
                () => Interlocked.Exchange(ref consumed, 1) == 0,
                actualClientProcessId);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response).AsMemory(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    internal static BridgeSetupHandshakeResponse ValidateRequest(
        BridgeSetupChallenge challenge,
        string? requestJson,
        DateTimeOffset now,
        Func<BridgeCandidateAttestation> observeBridge,
        Func<bool>? consumeNonce = null,
        int? actualClientProcessId = null)
    {
        const string denied = "身份握手未通过；不得执行任何 mutation。";
        if (now >= challenge.ExpiresAt || string.IsNullOrWhiteSpace(requestJson))
        {
            return Deny(challenge, denied);
        }

        BridgeCandidateAttestation liveBridge;
        try
        {
            liveBridge = observeBridge();
        }
        catch
        {
            return Deny(challenge, denied);
        }
        if (!MatchesLiveBridge(challenge, liveBridge))
        {
            return Deny(challenge, denied);
        }

        BridgeSetupHandshakeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeSetupHandshakeRequest>(requestJson);
        }
        catch (JsonException)
        {
            return Deny(challenge, denied);
        }

        if (request is null
            || !FileAttestation.FixedTimeEquals(request.Nonce, challenge.Nonce)
            || !request.Operation.Equals(VerifyOnlyOperation, StringComparison.Ordinal)
            || request.ClientProcessId <= 0
            || actualClientProcessId is int actual && actual != request.ClientProcessId
            || request.ClientProcessStartTimeUtcTicks <= 0
            || request.ClientExecutableSha256.Length != 64
            || !FileAttestation.FixedTimeEquals(
                request.ExpectedBridgePayloadManifestSha256,
                challenge.BridgePayloadManifestSha256)
            || challenge.BridgePayloadManifestSha256.Length != 64
            || challenge.BridgeWindowHandle == 0
            || !HasValidLocalPreviewAttestation(challenge))
        {
            return Deny(challenge, denied);
        }

        try
        {
            using Process client = Process.GetProcessById(request.ClientProcessId);
            string path = FileAttestation.CanonicalPath(client.MainModule?.FileName ?? string.Empty);
            bool sameStart = client.StartTime.ToUniversalTime().Ticks
                == request.ClientProcessStartTimeUtcTicks;
            bool samePath = path.Equals(
                Path.GetFullPath(request.ClientExecutablePath),
                StringComparison.OrdinalIgnoreCase);
            bool sameHash = FileAttestation.FixedTimeEquals(
                FileAttestation.Sha256(path),
                request.ClientExecutableSha256);
            if (!sameStart || !samePath || !sameHash || FileAttestation.HasReparsePoint(path))
            {
                return Deny(challenge, denied);
            }
        }
        catch
        {
            return Deny(challenge, denied);
        }

        if (consumeNonce is not null && !consumeNonce())
        {
            return Deny(challenge, denied);
        }

        return new BridgeSetupHandshakeResponse(
            true,
            challenge.Nonce,
            challenge.BridgeProcessId,
            challenge.BridgeProcessStartTimeUtcTicks,
            challenge.BridgeExecutableSha256,
            challenge.BridgePayloadManifestSha256,
            challenge.BridgeWindowHandle,
            challenge.ExpectedPageAutomationId,
            challenge.BridgeExecutablePath,
            challenge.BridgePackageInstallLocation,
            challenge.PackagingMode,
            challenge.BuildChannel,
            challenge.PackagePathAttested,
            challenge.DeveloperPathAttested,
            challenge.LocalPreviewSensitiveRuntimeEnabled,
            challenge.ProductionCredentialReuseEnabled,
            challenge.AgentMeshCredentialAccessEnabled,
            challenge.CredentialLockerResourcePrefix,
            challenge.CredentialLockerIsolated,
            challenge.PayloadManifestFiles,
            "只读身份握手通过；仅可在同一已核验 Bridge UI 中继续本机预览配置。" );
    }

    private static bool HasValidLocalPreviewAttestation(
        BridgeSetupChallenge challenge) =>
        HasValidPortableAttestation(challenge) || (challenge.PackagingMode.Equals("packaged", StringComparison.Ordinal)
        && challenge.BuildChannel.Equals("unsigned-local-preview", StringComparison.Ordinal)
        && challenge.PackagePathAttested
        && challenge.DeveloperPathAttested
        && challenge.LocalPreviewSensitiveRuntimeEnabled
        && !challenge.ProductionCredentialReuseEnabled
        && !challenge.AgentMeshCredentialAccessEnabled
        && challenge.CredentialLockerIsolated
        && challenge.CredentialLockerResourcePrefix.Equals(
            FeishuCredentialService.DevelopmentIsolatedResourcePrefix,
            StringComparison.Ordinal)
        && Path.GetFullPath(challenge.BridgePackageInstallLocation).Equals(
            Path.GetDirectoryName(Path.GetFullPath(challenge.BridgeExecutablePath)),
            StringComparison.OrdinalIgnoreCase)
        && challenge.PayloadManifestFiles.SequenceEqual(
            BridgeProductIdentity.SecurityCriticalPayloadFiles,
            StringComparer.Ordinal));

    private static bool HasValidPortableAttestation(BridgeSetupChallenge challenge)
    {
        if (!PortablePreview.Enabled || challenge.PackagingMode != PortablePreview.Channel
            || challenge.BuildChannel != PortablePreview.Channel || challenge.PackagePathAttested
            || challenge.DeveloperPathAttested || challenge.LocalPreviewSensitiveRuntimeEnabled
            || challenge.ProductionCredentialReuseEnabled || challenge.AgentMeshCredentialAccessEnabled
            || !challenge.CredentialLockerIsolated
            || challenge.CredentialLockerResourcePrefix != PortablePreview.CredentialPrefix)
            return false;
        try
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(challenge.BridgeExecutablePath))!;
            return Path.GetFullPath(challenge.BridgePackageInstallLocation) == root
                && PortablePreview.IsTrusted(root, challenge.BridgePayloadManifestSha256)
                && challenge.PayloadManifestFiles.SequenceEqual(PortablePreview.VerifyPayload(root), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private static bool MatchesLiveBridge(
        BridgeSetupChallenge challenge,
        BridgeCandidateAttestation live) =>
        live.ProcessId == challenge.BridgeProcessId
        && live.ProcessStartTimeUtcTicks == challenge.BridgeProcessStartTimeUtcTicks
        && live.WindowHandle == challenge.BridgeWindowHandle
        && live.PageAutomationId.Equals(
            challenge.ExpectedPageAutomationId,
            StringComparison.Ordinal)
        && live.ExecutablePath.Equals(
            challenge.BridgeExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && live.PackagingMode.Equals(
            challenge.PackagingMode,
            StringComparison.Ordinal)
        && live.PackagePathAttested == challenge.PackagePathAttested
        && live.DeveloperPathAttested == challenge.DeveloperPathAttested
        && FileAttestation.FixedTimeEquals(
            live.ExecutableSha256,
            challenge.BridgeExecutableSha256)
        && FileAttestation.FixedTimeEquals(
            live.PayloadManifestSha256,
            challenge.BridgePayloadManifestSha256);

    internal void RevokeActiveChallenge()
    {
        lock (_lock)
        {
            _activeCancellation?.Cancel();
            _acceptedChallenge = null;
            _acceptedCaller = null;
            _commandSessionToken = "";
            _commandReplayGuard.Clear();
        }
    }

    private static BridgeSetupHandshakeResponse Deny(
        BridgeSetupChallenge challenge,
        string message) => new(
            false,
            string.Empty,
            challenge.BridgeProcessId,
            challenge.BridgeProcessStartTimeUtcTicks,
            challenge.BridgeExecutableSha256,
            challenge.BridgePayloadManifestSha256,
            challenge.BridgeWindowHandle,
            challenge.ExpectedPageAutomationId,
            challenge.BridgeExecutablePath,
            challenge.BridgePackageInstallLocation,
            challenge.PackagingMode,
            challenge.BuildChannel,
            challenge.PackagePathAttested,
            challenge.DeveloperPathAttested,
            challenge.LocalPreviewSensitiveRuntimeEnabled,
            challenge.ProductionCredentialReuseEnabled,
            challenge.AgentMeshCredentialAccessEnabled,
            challenge.CredentialLockerResourcePrefix,
            challenge.CredentialLockerIsolated,
            challenge.PayloadManifestFiles,
            message);

    public async ValueTask DisposeAsync()
    {
        Task? listener;
        Task? commandListener;
        lock (_lock)
        {
            _activeCancellation?.Cancel();
            listener = _activeListener;
            commandListener = _activeCommandListener;
            _activeListener = null;
            _activeCommandListener = null;
            _acceptedChallenge = null;
            _acceptedCaller = null;
            _commandSessionToken = "";
            _commandReplayGuard.Clear();
        }

        if (listener is not null)
        {
            try { await listener; } catch { }
        }
        if (commandListener is not null)
        {
            try { await commandListener; } catch { }
        }

        lock (_lock)
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
