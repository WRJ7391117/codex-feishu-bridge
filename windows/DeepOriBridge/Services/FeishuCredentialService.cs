using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepOriBridge.Models;
using Windows.Security.Credentials;

namespace DeepOriBridge.Services;

public sealed class FeishuCredentialService
{
    private const string DefaultResourcePrefix = "DeepOriBridge.Windows.Feishu";
    internal const string DevelopmentIsolatedResourcePrefix =
        "DeepOriBridge.Windows.Development.Unsigned.Isolated";
    internal const string CleanFirstRunUiTestResourcePrefix =
        "DeepOriBridge.Windows.Tests.CleanFirstRunUi.";
    internal string ResourcePrefix { get; }
    internal bool UsesDevelopmentIsolatedNamespace =>
        _isolatedTestNamespace
        && ResourcePrefix.Equals(
            DevelopmentIsolatedResourcePrefix,
            StringComparison.Ordinal);
    internal bool UsesCleanFirstRunUiTestNamespace =>
        _isolatedTestNamespace
        && ResourcePrefix.StartsWith(
            CleanFirstRunUiTestResourcePrefix,
            StringComparison.Ordinal);
    internal bool IsSensitiveRuntimeNamespaceAllowed(bool developmentIdentity) =>
        !UsesCleanFirstRunUiTestNamespace
        && (!developmentIdentity || UsesDevelopmentIsolatedNamespace);
    private readonly string IdentityBundleResource;
    private readonly string CredentialResource;
    private readonly string OwnerCredentialResource;
    private readonly string ProjectCredentialResource;
    private readonly string VerifiedIdentityResource;
    private readonly string ActiveUserCredentialResource;
    private readonly string AuthorizationBundleResource;
    private readonly string LegacyCredentialResource;
    private readonly string LegacyOwnerCredentialResource;
    private const string TokenEndpoint =
        "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";
    private const string BotInfoEndpoint =
        "https://open.feishu.cn/open-apis/bot/v3/info";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly PasswordVault _vault = new();
    private readonly object _vaultGate = new();
    private readonly SemaphoreSlim _identityOperationGate = new(1, 1);
    private readonly bool _isolatedTestNamespace;

    private sealed record IdentityBundle(
        string AppId,
        string Secret,
        string BotOpenId,
        string BotDisplayName,
        string IdentityFingerprint,
        string Generation);

    private sealed record RemoteIdentity(
        string BotOpenId,
        string BotDisplayName,
        string IdentityFingerprint,
        int? ExpiresInSeconds);

    private sealed record AuthorizationBundle(
        string Scope,
        string Generation,
        string[] Owners,
        string ActiveSenderId,
        string? ProjectGeneration = null)
    {
        // Membership publication gets a new Credential Locker entry. Adding or
        // selecting an authorized owner must not invalidate every existing
        // owner's project/subscription scope. Explicit revoke still rotates it.
        internal string ProjectScopeGeneration => ProjectGeneration ?? Generation;
    }

    private enum IdentitySaveFaultPoint
    {
        None,
        AfterNewBundleAddedBeforeOldRemoval,
        AfterOldBundleRemovalBeforeLegacyCleanup,
        AfterBundlePublishBeforeAuthorizationCleanup,
    }

    private enum OwnerSaveFaultPoint
    {
        None,
        AfterEmptyBaselineAdded,
        BeforeOldBundleRemoval,
        AfterOldBundleRemoval,
    }

    private enum OwnerClearFaultPoint
    {
        None,
        AfterEmptyReplacementAdded,
        DuringOldBundleRemoval,
    }

    public FeishuCredentialService()
        : this(DefaultResourcePrefix, isolatedTestNamespace: false)
    {
    }

    internal FeishuCredentialService(string resourcePrefix)
        : this(resourcePrefix, isolatedTestNamespace: true)
    {
    }

    private FeishuCredentialService(string resourcePrefix, bool isolatedTestNamespace)
    {
        if (string.IsNullOrWhiteSpace(resourcePrefix))
        {
            throw new ArgumentException("Credential Locker resource prefix 不能为空。", nameof(resourcePrefix));
        }

        _isolatedTestNamespace = isolatedTestNamespace;
        ResourcePrefix = resourcePrefix;
        IdentityBundleResource = resourcePrefix + ".IdentityBundle.v3";
        CredentialResource = resourcePrefix + ".BotCredential.v2";
        OwnerCredentialResource = resourcePrefix + ".AuthorizedUser.v2";
        ProjectCredentialResource = resourcePrefix + ".ProjectAuthorization.v2";
        VerifiedIdentityResource = resourcePrefix + ".VerifiedAppIdentity.v2";
        ActiveUserCredentialResource = resourcePrefix + ".ActiveAuthorizedUser.v2";
        AuthorizationBundleResource = resourcePrefix + ".AuthorizationBundle.v3";
        LegacyCredentialResource = isolatedTestNamespace
            ? resourcePrefix + ".Legacy.TestBot"
            : "DeepOriBridge.Feishu.TestBot";
        LegacyOwnerCredentialResource = isolatedTestNamespace
            ? resourcePrefix + ".Legacy.TestBot.Owner"
            : "DeepOriBridge.Feishu.TestBot.Owner";
    }

    public FeishuStoredCredential Load()
    {
        try
        {
            IReadOnlyList<PasswordCredential> bundles = FindIdentityBundles();
            if (bundles.Count > 1)
            {
                return new FeishuStoredCredential(
                    string.Empty,
                    false,
                    "检测到多个飞书身份世代；已 fail-closed，请显式重置后重新验证。");
            }

            if (bundles.Count == 1)
            {
                IdentityBundle? bundle = ReadIdentityBundle(bundles[0]);
                if (bundle is null)
                {
                    return new FeishuStoredCredential(
                        string.Empty,
                        false,
                        "飞书身份世代记录损坏或不一致；已禁止连接。");
                }

                return new FeishuStoredCredential(
                    bundle.AppId,
                    !string.IsNullOrEmpty(bundle.Secret),
                    HasVerifiedIdentity: true,
                    AppFingerprint: ShortFingerprint(bundle.IdentityFingerprint),
                    BotDisplayName: bundle.BotDisplayName,
                    IdentityGeneration: bundle.Generation,
                    FullIdentityFingerprint: bundle.IdentityFingerprint);
            }

            PasswordCredential? credential = FindCredentials().FirstOrDefault();
            bool legacy = false;
            if (credential is null)
            {
                credential = FindLegacyCredentials().FirstOrDefault();
                legacy = credential is not null;
            }
            if (credential is null)
            {
                return new FeishuStoredCredential(string.Empty, false);
            }

            credential.RetrievePassword();
            return new FeishuStoredCredential(
                credential.UserName,
                !string.IsNullOrEmpty(credential.Password),
                HasVerifiedIdentity: false,
                AppFingerprint: string.Empty);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return new FeishuStoredCredential(string.Empty, false);
        }
        catch (Exception exception)
        {
            return new FeishuStoredCredential(
                string.Empty,
                false,
                $"无法读取 Windows Credential Locker：{exception.GetType().Name}");
        }
    }

    public async Task<FeishuVerificationResult> VerifyAndSaveAsync(
        string appId,
        string suppliedSecret,
        CancellationToken cancellationToken)
    {
        await _identityOperationGate.WaitAsync(cancellationToken);
        try
        {
            return await VerifyAndSaveCoreAsync(appId, suppliedSecret, cancellationToken);
        }
        finally
        {
            _identityOperationGate.Release();
        }
    }

    private async Task<FeishuVerificationResult> VerifyAndSaveCoreAsync(
        string appId,
        string suppliedSecret,
        CancellationToken cancellationToken)
    {
        string normalizedAppId = appId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAppId))
        {
            return new FeishuVerificationResult(
                false,
                "请输入 App ID。",
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }

        string secret = suppliedSecret;
        if (string.IsNullOrEmpty(secret))
        {
            secret = RetrieveSavedSecret(normalizedAppId) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(secret))
        {
            return new FeishuVerificationResult(
                false,
                "请输入 App Secret；它不会显示在页面或日志中。",
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }

        bool credentialWriteSucceeded = false;
        try
        {
            RemoteIdentity identity = await QueryRemoteIdentityAsync(
                normalizedAppId,
                secret,
                cancellationToken);
            SaveIdentityBundle(
                normalizedAppId,
                secret,
                identity,
                out credentialWriteSucceeded);
            return new FeishuVerificationResult(
                true,
                $"凭证有效；已通过官方 bot-info 验证机器人“{identity.BotDisplayName}”。临时 token 未保存、未显示。",
                identity.ExpiresInSeconds,
                identity.BotDisplayName,
                ShortFingerprint(identity.IdentityFingerprint),
                CredentialWriteSucceeded: true,
                CurrentlyVerified: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return new FeishuVerificationResult(
                false,
                $"无法连接飞书开放平台：{exception.GetType().Name}",
                CredentialWriteSucceeded: credentialWriteSucceeded,
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }
        catch (JsonException)
        {
            return new FeishuVerificationResult(
                false,
                "飞书返回了无法识别的验证响应。",
                CredentialWriteSucceeded: credentialWriteSucceeded,
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }
        catch (InvalidOperationException exception)
        {
            return new FeishuVerificationResult(
                false,
                exception.Message,
                CredentialWriteSucceeded: credentialWriteSucceeded,
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }
        catch (Exception exception)
        {
            return new FeishuVerificationResult(
                false,
                $"Credential Locker 提交未完成：{exception.GetType().Name}",
                CredentialWriteSucceeded: credentialWriteSucceeded,
                CurrentlyVerified: FirstConnectionNavigationGate.IsCurrentlyVerified(Load()));
        }
        finally
        {
            secret = string.Empty;
        }
    }

    private static async Task<RemoteIdentity> QueryRemoteIdentityAsync(
        string appId,
        string secret,
        CancellationToken cancellationToken)
    {
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = JsonContent.Create(new
            {
                app_id = appId,
                app_secret = secret,
            }),
        };
        using HttpResponseMessage tokenResponse = await HttpClient.SendAsync(
            tokenRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string tokenPayload = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument tokenDocument = JsonDocument.Parse(tokenPayload);
        JsonElement tokenRoot = tokenDocument.RootElement;
        int tokenCode = ReadCode(tokenRoot);
        string tokenMessage = ReadMessage(tokenRoot);
        string tenantToken = tokenRoot.TryGetProperty(
            "tenant_access_token",
            out JsonElement tokenElement)
                ? tokenElement.GetString() ?? string.Empty
                : string.Empty;
        if (!tokenResponse.IsSuccessStatusCode || tokenCode != 0 || tenantToken.Length == 0)
        {
            throw new InvalidOperationException(
                $"飞书凭证验证失败（code {tokenCode}）：{Redact(tokenMessage, appId, secret)}");
        }

        int? expiresIn = tokenRoot.TryGetProperty("expire", out JsonElement expireElement)
            && expireElement.TryGetInt32(out int parsedExpire)
                ? parsedExpire
                : null;
        using var botRequest = new HttpRequestMessage(HttpMethod.Get, BotInfoEndpoint);
        botRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantToken);
        using HttpResponseMessage botResponse = await HttpClient.SendAsync(
            botRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string botPayload = await botResponse.Content.ReadAsStringAsync(cancellationToken);
        tenantToken = string.Empty;
        using JsonDocument botDocument = JsonDocument.Parse(botPayload);
        JsonElement botRoot = botDocument.RootElement;
        int botCode = ReadCode(botRoot);
        if (!botResponse.IsSuccessStatusCode
            || botCode != 0
            || !botRoot.TryGetProperty("bot", out JsonElement bot)
            || bot.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"官方 bot-info 身份验证失败（code {botCode}）；不会保存或启动连接。");
        }

        string botOpenId = bot.TryGetProperty("open_id", out JsonElement openIdElement)
            ? openIdElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        string botDisplayName = bot.TryGetProperty("app_name", out JsonElement nameElement)
            ? nameElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (botOpenId.Length == 0
            || botOpenId.Length > 256
            || botDisplayName.Length == 0)
        {
            throw new InvalidOperationException(
                "官方 bot-info 未返回可比较的 bot/tenant 身份；连接保持禁用。");
        }

        if (botDisplayName.Length > 120)
        {
            throw new InvalidOperationException(
                "官方 bot-info 返回的名称超出可比较范围；连接保持禁用。");
        }

        return new RemoteIdentity(
            botOpenId,
            botDisplayName,
            FeishuAppIdentityPolicy.Fingerprint(appId, botOpenId, botDisplayName),
            expiresIn);
    }

    private static int ReadCode(JsonElement root) =>
        root.TryGetProperty("code", out JsonElement codeElement)
        && codeElement.TryGetInt32(out int code)
            ? code
            : -1;

    private static string ReadMessage(JsonElement root) =>
        root.TryGetProperty("msg", out JsonElement messageElement)
            ? messageElement.GetString() ?? "飞书没有返回错误说明。"
            : "飞书没有返回错误说明。";

    public void Clear()
    {
        foreach (PasswordCredential credential in FindIdentityBundles())
        {
            _vault.Remove(credential);
        }
        foreach (PasswordCredential credential in FindCredentials())
        {
            _vault.Remove(credential);
        }
        foreach (PasswordCredential credential in FindLegacyCredentials())
        {
            _vault.Remove(credential);
        }
        foreach (PasswordCredential credential in FindLegacyOwnerCredentials())
        {
            _vault.Remove(credential);
        }

        ClearOwnerBinding();
        foreach (PasswordCredential credential in FindAuthorizationBundles())
        {
            TryRemoveCredential(credential);
        }
        foreach (PasswordCredential credential in FindVerifiedIdentityCredentials())
        {
            _vault.Remove(credential);
        }
    }

    public FeishuOwnerBindingState LoadOwnerBinding()
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        if (bundle is null)
        {
            return new FeishuOwnerBindingState(false);
        }

        try
        {
            string scope = AuthorizationScope(bundle);
            AuthorizationBundle? authorization = LoadSingleAuthorizationBundle(scope);
            if (authorization is null || authorization.Owners.Length == 0)
            {
                return new FeishuOwnerBindingState(false);
            }
            return new FeishuOwnerBindingState(
                true,
                UserCount: authorization.Owners.Length,
                ActiveUserFingerprint: string.IsNullOrWhiteSpace(authorization.ActiveSenderId)
                    ? string.Empty
                    : ShortFingerprint(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authorization.ActiveSenderId)))));
        }
        catch (Exception exception)
        {
            return new FeishuOwnerBindingState(
                false,
                $"无法读取 owner 白名单：{exception.GetType().Name}");
        }
    }

    public void BindOwner(string senderId) =>
        BindOwner(senderId, OwnerSaveFaultPoint.None);

    private void BindOwner(string senderId, OwnerSaveFaultPoint faultPoint)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            throw new InvalidOperationException("飞书事件缺少可绑定的 sender ID。");
        }

        IdentityBundle? identity = LoadSingleIdentityBundle();
        if (identity is null)
        {
            throw new InvalidOperationException("请先保存并验证飞书测试机器人凭证。");
        }

        string scope = AuthorizationScope(identity);
        lock (_vaultGate)
        {
            PasswordCredential[] currentCredentials = FindAuthorizationBundles().ToArray();
            AuthorizationBundle? current = currentCredentials.Length == 1
                ? ReadAuthorizationBundle(currentCredentials[0], scope)
                : null;
            if (currentCredentials.Length > 1
                || currentCredentials.Length == 1 && current is null)
            {
                throw new InvalidOperationException("owner 授权世代不唯一或无效；已 fail-closed，请撤销后重新绑定。");
            }

            if (current is null)
            {
                current = NewAuthorizationBundle(scope, Array.Empty<string>(), string.Empty);
                _vault.Add(ToCredential(current));
                currentCredentials = FindAuthorizationBundles().ToArray();
                if (faultPoint == OwnerSaveFaultPoint.AfterEmptyBaselineAdded)
                {
                    throw new InvalidOperationException("Injected interruption after empty authorization baseline.");
                }
            }

            string[] owners = current.Owners
                .Append(senderId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            AuthorizationBundle replacement = NewAuthorizationBundle(scope, owners, senderId)
                with { ProjectGeneration = current.ProjectScopeGeneration };
            _vault.Add(ToCredential(replacement));
            Exception? removalFailure = null;
            foreach (PasswordCredential credential in currentCredentials)
            {
                try
                {
                    if (faultPoint == OwnerSaveFaultPoint.BeforeOldBundleRemoval)
                    {
                        throw new InvalidOperationException(
                            "Injected interruption before old authorization removal.");
                    }

                    _vault.Remove(credential);
                    if (faultPoint == OwnerSaveFaultPoint.AfterOldBundleRemoval)
                    {
                        throw new InvalidOperationException(
                            "Injected interruption after old authorization removal took effect.");
                    }
                }
                catch (Exception exception)
                {
                    removalFailure ??= exception;
                }
            }

            AuthorizationBundle? committed = LoadSingleAuthorizationBundle(scope);
            if (committed is not null
                && string.Equals(
                    committed.Generation,
                    replacement.Generation,
                    StringComparison.Ordinal)
                && AuthorizationContainsOwner(committed, senderId))
            {
                return;
            }

            throw new InvalidOperationException(
                "owner 授权 replacement 未形成唯一持久世代；保持 fail-closed。",
                removalFailure);
        }
    }

    public void ClearOwnerBinding() =>
        ClearOwnerBinding(OwnerClearFaultPoint.None);

    private void ClearOwnerBinding(OwnerClearFaultPoint faultPoint)
    {
        IdentityBundle? identity = LoadSingleIdentityBundle();
        string scope = identity is null
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            : AuthorizationScope(identity);
        lock (_vaultGate)
        {
            PasswordCredential[] oldBundles = FindAuthorizationBundles().ToArray();
            AuthorizationBundle empty = NewAuthorizationBundle(
                scope,
                Array.Empty<string>(),
                string.Empty);
            _vault.Add(ToCredential(empty));
            if (faultPoint == OwnerClearFaultPoint.AfterEmptyReplacementAdded)
            {
                throw new InvalidOperationException("Injected interruption after empty authorization replacement.");
            }

            for (int index = 0; index < oldBundles.Length; index++)
            {
                _vault.Remove(oldBundles[index]);
                if (faultPoint == OwnerClearFaultPoint.DuringOldBundleRemoval && index == 0)
                {
                    throw new InvalidOperationException("Injected interruption during old authorization removal.");
                }
            }
        }

        foreach (PasswordCredential credential in FindOwnerCredentials())
        {
            TryRemoveCredential(credential);
        }
        foreach (PasswordCredential credential in FindProjectCredentials())
        {
            TryRemoveCredential(credential);
        }
        foreach (PasswordCredential credential in FindActiveUserCredentials())
        {
            TryRemoveCredential(credential);
        }
    }

    public FeishuProjectAuthorizationState LoadProjectAuthorization()
    {
        try
        {
            IdentityBundle? bundle = LoadSingleIdentityBundle();
            AuthorizationBundle? authorization = bundle is null
                ? null
                : LoadSingleAuthorizationBundle(AuthorizationScope(bundle));
            string? senderId = authorization?.ActiveSenderId;
            if (string.IsNullOrWhiteSpace(senderId)
                || bundle is null
                || authorization is null)
            {
                return new FeishuProjectAuthorizationState(false, string.Empty);
            }

            return LoadProjectAuthorizationByKey(
                AuthorizationKey(
                    AuthorizationScope(bundle),
                    authorization.ProjectScopeGeneration,
                    senderId));
        }
        catch (Exception exception)
        {
            return new FeishuProjectAuthorizationState(
                false,
                string.Empty,
                $"无法读取项目授权：{exception.GetType().Name}");
        }
    }

    private FeishuProjectAuthorizationState LoadProjectAuthorizationByKey(string key)
    {
        PasswordCredential? credential = FindProjectCredentials()
            .FirstOrDefault(item => item.UserName.Equals(key, StringComparison.Ordinal));
        if (credential is null)
        {
            return new FeishuProjectAuthorizationState(false, string.Empty);
        }

        credential.RetrievePassword();
        string path = credential.Password;
        bool valid = !string.IsNullOrWhiteSpace(path)
            && !path.Contains('*', StringComparison.Ordinal)
            && Path.IsPathFullyQualified(path)
            && Directory.Exists(path);
        return new FeishuProjectAuthorizationState(valid, valid ? path : string.Empty);
    }

    public FeishuProjectAuthorizationState SaveProjectAuthorization(string projectPath)
    {
        string candidate = projectPath.Trim();
        if (candidate.Length == 0 || candidate.Contains('*', StringComparison.Ordinal))
        {
            return new FeishuProjectAuthorizationState(false, string.Empty, "必须选择一个明确项目，不能留空或使用通配符。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return new FeishuProjectAuthorizationState(false, string.Empty, "项目路径无效。");
        }

        if (!Directory.Exists(fullPath))
        {
            return new FeishuProjectAuthorizationState(false, string.Empty, "项目目录不存在。");
        }

        IdentityBundle? bundle = LoadSingleIdentityBundle();
        AuthorizationBundle? authorization = bundle is null
            ? null
            : LoadSingleAuthorizationBundle(AuthorizationScope(bundle));
        string? senderId = authorization?.ActiveSenderId;
        if (string.IsNullOrWhiteSpace(senderId)
            || bundle is null
            || authorization is null
            || !AuthorizationContainsOwner(authorization, senderId))
        {
            return new FeishuProjectAuthorizationState(false, string.Empty, "请先通过一次性验证码绑定飞书用户。");
        }

        string key = AuthorizationKey(
            AuthorizationScope(bundle),
            authorization.ProjectScopeGeneration,
            senderId);
        foreach (PasswordCredential credential in FindProjectCredentials()
            .Where(item => item.UserName.Equals(key, StringComparison.Ordinal)))
        {
            _vault.Remove(credential);
        }
        _vault.Add(new PasswordCredential(ProjectCredentialResource, key, fullPath));
        return new FeishuProjectAuthorizationState(true, fullPath);
    }

    internal bool MatchesBoundOwner(string senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            return false;
        }

        IdentityBundle? bundle = LoadSingleIdentityBundle();
        if (bundle is null)
        {
            return false;
        }

        return MatchesBoundOwner(senderId, bundle);
    }

    private bool MatchesBoundOwner(string senderId, IdentityBundle bundle)
    {
        AuthorizationBundle? authorization = LoadSingleAuthorizationBundle(
            AuthorizationScope(bundle));
        return authorization is not null
            && AuthorizationContainsOwner(authorization, senderId);
    }

    internal FeishuProjectAuthorizationState LoadProjectAuthorization(string senderId)
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        if (bundle is null || !MatchesBoundOwner(senderId, bundle))
        {
            return new FeishuProjectAuthorizationState(false, string.Empty);
        }
        AuthorizationBundle? authorization = LoadSingleAuthorizationBundle(
            AuthorizationScope(bundle));
        if (authorization is null)
        {
            return new FeishuProjectAuthorizationState(false, string.Empty);
        }
        return LoadProjectAuthorizationByKey(
            AuthorizationKey(
                AuthorizationScope(bundle),
                authorization.ProjectScopeGeneration,
                senderId));
    }

    internal string? CurrentTaskSelectionScope() => CurrentTaskSelectionScope(null);

    internal string? CurrentTaskSelectionScope(string? senderId)
    {
        lock (_vaultGate)
        {
            IdentityBundle? identity = LoadSingleIdentityBundle();
            if (identity is null) return null;
            string identityScope = AuthorizationScope(identity);
            AuthorizationBundle? authorization = LoadSingleAuthorizationBundle(identityScope);
            if (authorization is null || !AuthorizationContainsOwner(authorization, senderId ?? authorization.ActiveSenderId))
                return null;
            string ownerKey = AuthorizationKey(identityScope, authorization.ProjectScopeGeneration, senderId ?? authorization.ActiveSenderId);
            FeishuProjectAuthorizationState project = LoadProjectAuthorizationByKey(ownerKey);
            if (!project.IsAuthorized) return null;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { ResourcePrefix, ownerKey,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.ProjectPath)).ToUpperInvariant() }))));
        }
    }

    internal bool HasAnyProjectAuthorization()
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        if (bundle is null)
        {
            return false;
        }

        string scope = AuthorizationScope(bundle);
        AuthorizationBundle? authorization = LoadSingleAuthorizationBundle(scope);
        return authorization is not null
            && authorization.Owners.Any(senderId =>
                LoadProjectAuthorizationByKey(
                    AuthorizationKey(scope, authorization.ProjectScopeGeneration, senderId)).IsAuthorized);
    }

    internal string? ActiveBoundOwnerId()
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        string? sender = bundle is null ? null : RetrieveActiveUserId(bundle);
        return sender is not null && bundle is not null && MatchesBoundOwner(sender, bundle) ? sender : null;
    }

    internal string[] BoundOwnerIds()
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        return bundle is null ? [] : LoadSingleAuthorizationBundle(AuthorizationScope(bundle))?.Owners.ToArray() ?? [];
    }

    private string? RetrieveActiveUserId(IdentityBundle bundle)
    {
        return LoadSingleAuthorizationBundle(AuthorizationScope(bundle))?.ActiveSenderId;
    }

    private static string AuthorizationKey(
        string scope,
        string authorizationGeneration,
        string senderId)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(senderId));
        return $"{scope}:{authorizationGeneration}:{Convert.ToHexString(hash)}";
    }

    private static string AuthorizationScope(IdentityBundle bundle)
    {
        byte[] material = Encoding.UTF8.GetBytes(
            $"DeepOriBridge.Windows.Authorization.v3\n{bundle.AppId}\n{bundle.IdentityFingerprint}");
        return Convert.ToHexString(SHA256.HashData(material));
    }

    internal async Task<FeishuRuntimeCredential?> LoadForRuntimeAsync(
        string intendedAppId,
        CancellationToken cancellationToken)
    {
        await _identityOperationGate.WaitAsync(cancellationToken);
        try
        {
            IdentityBundle? bundle = LoadSingleIdentityBundle();
            if (bundle is null
                || !bundle.AppId.Equals(intendedAppId.Trim(), StringComparison.Ordinal))
            {
                return null;
            }

            RemoteIdentity remote = await QueryRemoteIdentityAsync(
                bundle.AppId,
                bundle.Secret,
                cancellationToken);
            if (!FileAttestation.FixedTimeEquals(
                    bundle.IdentityFingerprint,
                    remote.IdentityFingerprint))
            {
                InvalidateIdentityGeneration();
                throw new InvalidOperationException(
                    "官方 bot/tenant 身份已变化；旧世代、owner、项目授权与路由票据已清除，请重新验证。" );
            }

            IdentityBundle? active = LoadSingleIdentityBundle();
            if (active is null
                || !active.Generation.Equals(bundle.Generation, StringComparison.Ordinal)
                || !FileAttestation.FixedTimeEquals(
                    active.IdentityFingerprint,
                    bundle.IdentityFingerprint))
            {
                return null;
            }

            return new FeishuRuntimeCredential(
                bundle.AppId,
                bundle.Secret,
                bundle.BotDisplayName,
                bundle.IdentityFingerprint,
                bundle.Generation);
        }
        finally
        {
            _identityOperationGate.Release();
        }
    }

    internal bool MatchesActiveGeneration(FeishuRuntimeCredential credential)
        => MatchesActiveGeneration(
            credential.AppId,
            credential.IdentityFingerprint,
            credential.IdentityGeneration);

    internal FeishuRuntimeCredential? LoadRuntimeCredentialForTests()
    {
        EnsureIsolatedTestNamespace();
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        return bundle is null
            ? null
            : new FeishuRuntimeCredential(
                bundle.AppId,
                bundle.Secret,
                bundle.BotDisplayName,
                bundle.IdentityFingerprint,
                bundle.Generation);
    }

    internal bool MatchesActiveGeneration(
        string appId,
        string identityFingerprint,
        string identityGeneration)
    {
        IdentityBundle? active = LoadSingleIdentityBundle();
        return active is not null
            && active.AppId.Equals(appId, StringComparison.Ordinal)
            && active.Generation.Equals(identityGeneration, StringComparison.Ordinal)
            && FileAttestation.FixedTimeEquals(
                active.IdentityFingerprint,
                identityFingerprint);
    }

    private void SaveIdentityBundle(
        string appId,
        string secret,
        RemoteIdentity identity,
        IdentitySaveFaultPoint faultPoint = IdentitySaveFaultPoint.None)
    {
        SaveIdentityBundle(
            appId,
            secret,
            identity,
            out _,
            faultPoint);
    }

    private void SaveIdentityBundle(
        string appId,
        string secret,
        RemoteIdentity identity,
        out bool credentialWriteSucceeded,
        IdentitySaveFaultPoint faultPoint = IdentitySaveFaultPoint.None)
    {
        credentialWriteSucceeded = false;
        IdentityBundle? previous = LoadSingleIdentityBundle();
        string generation = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var bundle = new IdentityBundle(
            appId,
            secret,
            identity.BotOpenId,
            identity.BotDisplayName,
            identity.IdentityFingerprint,
            generation);
        string json = JsonSerializer.Serialize(bundle);
        lock (_vaultGate)
        {
            _vault.Add(new PasswordCredential(IdentityBundleResource, generation, json));
            credentialWriteSucceeded = true;
            if (faultPoint == IdentitySaveFaultPoint.AfterNewBundleAddedBeforeOldRemoval)
            {
                throw new InvalidOperationException(
                    "Injected interruption after adding a new identity bundle.");
            }

            foreach (PasswordCredential credential in FindIdentityBundles()
                .Where(item => !item.UserName.Equals(generation, StringComparison.Ordinal)))
            {
                _vault.Remove(credential);
            }
            if (faultPoint == IdentitySaveFaultPoint.AfterOldBundleRemovalBeforeLegacyCleanup)
            {
                throw new InvalidOperationException(
                    "Injected interruption after replacing the active identity bundle.");
            }

            foreach (PasswordCredential credential in FindCredentials())
            {
                _vault.Remove(credential);
            }
            foreach (PasswordCredential credential in FindLegacyCredentials())
            {
                _vault.Remove(credential);
            }
            foreach (PasswordCredential credential in FindVerifiedIdentityCredentials())
            {
                _vault.Remove(credential);
            }
        }

        if (faultPoint == IdentitySaveFaultPoint.AfterBundlePublishBeforeAuthorizationCleanup)
        {
            throw new InvalidOperationException(
                "Injected interruption after identity publication for crash-consistency testing.");
        }

        if (previous is null
            || !FileAttestation.FixedTimeEquals(
                previous.IdentityFingerprint,
                identity.IdentityFingerprint))
        {
            ClearOwnerBinding();
        }
    }

    internal void InvalidateIdentityGeneration()
    {
        lock (_vaultGate)
        {
            foreach (PasswordCredential credential in FindIdentityBundles())
            {
                _vault.Remove(credential);
            }
        }

        ClearOwnerBinding();
        foreach (PasswordCredential credential in FindAuthorizationBundles())
        {
            TryRemoveCredential(credential);
        }
    }

    private string? RetrieveSavedSecret(string appId)
    {
        IdentityBundle? bundle = LoadSingleIdentityBundle();
        if (bundle is not null
            && bundle.AppId.Equals(appId, StringComparison.Ordinal))
        {
            return bundle.Secret;
        }

        return null;
    }

    private IReadOnlyList<PasswordCredential> FindCredentials()
    {
        try
        {
            return _vault.FindAllByResource(CredentialResource);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return Array.Empty<PasswordCredential>();
        }
    }

    private IReadOnlyList<PasswordCredential> FindIdentityBundles() =>
        FindByResourceOrEmpty(IdentityBundleResource);

    private IdentityBundle? LoadSingleIdentityBundle()
    {
        IReadOnlyList<PasswordCredential> credentials = FindIdentityBundles();
        return credentials.Count == 1 ? ReadIdentityBundle(credentials[0]) : null;
    }

    private static IdentityBundle? ReadIdentityBundle(PasswordCredential credential)
    {
        try
        {
            credential.RetrievePassword();
            IdentityBundle? bundle = JsonSerializer.Deserialize<IdentityBundle>(credential.Password);
            if (bundle is null
                || bundle.AppId.Length == 0
                || bundle.Secret.Length == 0
                || bundle.BotOpenId.Length == 0
                || bundle.BotDisplayName.Length == 0
                || bundle.IdentityFingerprint.Length != 64
                || bundle.Generation.Length != 32
                || !credential.UserName.Equals(bundle.Generation, StringComparison.Ordinal)
                || !FeishuAppIdentityPolicy.Matches(
                    bundle.AppId,
                    bundle.BotOpenId,
                    bundle.BotDisplayName,
                    bundle.IdentityFingerprint))
            {
                return null;
            }

            return bundle;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyList<PasswordCredential> FindOwnerCredentials()
    {
        try
        {
            return _vault.FindAllByResource(OwnerCredentialResource);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return Array.Empty<PasswordCredential>();
        }
    }

    private IReadOnlyList<PasswordCredential> FindAuthorizationBundles() =>
        FindByResourceOrEmpty(AuthorizationBundleResource);

    private AuthorizationBundle? LoadSingleAuthorizationBundle(string expectedScope)
    {
        IReadOnlyList<PasswordCredential> credentials = FindAuthorizationBundles();
        return credentials.Count == 1
            ? ReadAuthorizationBundle(credentials[0], expectedScope)
            : null;
    }

    private static AuthorizationBundle NewAuthorizationBundle(
        string scope,
        string[] owners,
        string activeSenderId) =>
        new(
            scope,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            owners,
            activeSenderId);

    private PasswordCredential ToCredential(AuthorizationBundle bundle) =>
        new(
            AuthorizationBundleResource,
            bundle.Generation,
            JsonSerializer.Serialize(bundle));

    private static AuthorizationBundle? ReadAuthorizationBundle(
        PasswordCredential credential,
        string expectedScope)
    {
        try
        {
            credential.RetrievePassword();
            AuthorizationBundle? bundle = JsonSerializer.Deserialize<AuthorizationBundle>(
                credential.Password);
            if (bundle is null
                || bundle.Scope is not { Length: 64 }
                || !string.Equals(bundle.Scope, expectedScope, StringComparison.Ordinal)
                || bundle.Generation is not { Length: 32 }
                || (bundle.ProjectGeneration is not null && (bundle.ProjectGeneration.Length != 32
                    || !bundle.ProjectGeneration.All(char.IsAsciiHexDigit)))
                || !string.Equals(
                    credential.UserName,
                    bundle.Generation,
                    StringComparison.Ordinal)
                || bundle.Owners is null
                || bundle.ActiveSenderId is null
                || bundle.Owners.Length > 64
                || bundle.Owners.Any(string.IsNullOrWhiteSpace)
                || bundle.Owners.Distinct(StringComparer.Ordinal).Count() != bundle.Owners.Length
                || (bundle.Owners.Length == 0 && bundle.ActiveSenderId.Length != 0)
                || (bundle.Owners.Length > 0
                    && !AuthorizationContainsOwner(bundle, bundle.ActiveSenderId)))
            {
                return null;
            }

            return bundle;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool AuthorizationContainsOwner(
        AuthorizationBundle authorization,
        string senderId)
    {
        byte[] candidate = Encoding.UTF8.GetBytes(senderId);
        return authorization.Owners.Any(owner =>
        {
            byte[] expected = Encoding.UTF8.GetBytes(owner);
            return expected.Length == candidate.Length
                && CryptographicOperations.FixedTimeEquals(expected, candidate);
        });
    }

    private void TryRemoveCredential(PasswordCredential credential)
    {
        try
        {
            _vault.Remove(credential);
        }
        catch
        {
            // Legacy v2 entries are never consulted by the v3 authorization bundle.
        }
    }

    private IReadOnlyList<PasswordCredential> FindLegacyCredentials() =>
        FindByResourceOrEmpty(LegacyCredentialResource);

    private IReadOnlyList<PasswordCredential> FindLegacyOwnerCredentials() =>
        FindByResourceOrEmpty(LegacyOwnerCredentialResource);

    private IReadOnlyList<PasswordCredential> FindByResourceOrEmpty(string resource)
    {
        try
        {
            return _vault.FindAllByResource(resource);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return Array.Empty<PasswordCredential>();
        }
    }

    private IReadOnlyList<PasswordCredential> FindProjectCredentials()
    {
        try
        {
            return _vault.FindAllByResource(ProjectCredentialResource);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return Array.Empty<PasswordCredential>();
        }
    }

    private IReadOnlyList<PasswordCredential> FindActiveUserCredentials() =>
        FindByResourceOrEmpty(ActiveUserCredentialResource);

    private IReadOnlyList<PasswordCredential> FindVerifiedIdentityCredentials()
    {
        try
        {
            return _vault.FindAllByResource(VerifiedIdentityResource);
        }
        catch (Exception exception) when (IsCredentialNotFound(exception))
        {
            return Array.Empty<PasswordCredential>();
        }
    }

    internal static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length >= 12 ? fingerprint[..12] : fingerprint;

    internal void SeedLegacyCredentialForTests(string appId, string secret)
    {
        EnsureIsolatedTestNamespace();
        _vault.Add(new PasswordCredential(CredentialResource, appId, secret));
    }

    internal void PromoteIdentityForTests(
        string appId,
        string secret,
        string botOpenId,
        string botDisplayName)
    {
        EnsureIsolatedTestNamespace();
        SaveIdentityBundle(
            appId,
            secret,
            new RemoteIdentity(
                botOpenId,
                botDisplayName,
                FeishuAppIdentityPolicy.Fingerprint(appId, botOpenId, botDisplayName),
                7200));
    }

    internal void PromoteIdentityWithInterruptedCleanupForTests(
        string appId,
        string secret,
        string botOpenId,
        string botDisplayName,
        int faultStep = 3)
    {
        EnsureIsolatedTestNamespace();
        SaveIdentityBundle(
            appId,
            secret,
            new RemoteIdentity(
                botOpenId,
                botDisplayName,
                FeishuAppIdentityPolicy.Fingerprint(appId, botOpenId, botDisplayName),
                7200),
            faultStep switch
            {
                1 => IdentitySaveFaultPoint.AfterNewBundleAddedBeforeOldRemoval,
                2 => IdentitySaveFaultPoint.AfterOldBundleRemovalBeforeLegacyCleanup,
                3 => IdentitySaveFaultPoint.AfterBundlePublishBeforeAuthorizationCleanup,
                _ => throw new ArgumentOutOfRangeException(nameof(faultStep)),
            });
    }

    internal void BindOwnerWithInterruptedCommitForTests(
        string senderId,
        int faultStep)
    {
        EnsureIsolatedTestNamespace();
        if (faultStep == 1)
        {
            foreach (PasswordCredential credential in FindAuthorizationBundles())
            {
                _vault.Remove(credential);
            }
        }
        BindOwner(
            senderId,
            faultStep switch
            {
                1 => OwnerSaveFaultPoint.AfterEmptyBaselineAdded,
                2 => OwnerSaveFaultPoint.BeforeOldBundleRemoval,
                3 => OwnerSaveFaultPoint.AfterOldBundleRemoval,
                _ => throw new ArgumentOutOfRangeException(nameof(faultStep)),
            });
    }

    internal void ClearOwnerBindingWithInterruptedCommitForTests(int faultStep)
    {
        EnsureIsolatedTestNamespace();
        ClearOwnerBinding(
            faultStep switch
            {
                1 => OwnerClearFaultPoint.AfterEmptyReplacementAdded,
                2 => OwnerClearFaultPoint.DuringOldBundleRemoval,
                _ => throw new ArgumentOutOfRangeException(nameof(faultStep)),
            });
    }

    internal int IsolatedEntryCountForTests()
    {
        EnsureIsolatedTestNamespace();
        return FindIdentityBundles().Count
            + FindCredentials().Count
            + FindOwnerCredentials().Count
            + FindProjectCredentials().Count
            + FindVerifiedIdentityCredentials().Count
            + FindActiveUserCredentials().Count
            + FindAuthorizationBundles().Count
            + FindLegacyCredentials().Count
            + FindLegacyOwnerCredentials().Count;
    }

    private void EnsureIsolatedTestNamespace()
    {
        if (!_isolatedTestNamespace)
        {
            throw new InvalidOperationException("真实 Bridge vault 命名空间禁止测试注入。" );
        }
    }

    private static bool IsCredentialNotFound(Exception exception) =>
        exception.HResult == unchecked((int)0x80070490);

    private static string Redact(string value, string appId, string secret)
    {
        string redacted = value
            .Replace(appId, "[App ID]", StringComparison.Ordinal)
            .Replace(secret, "[App Secret]", StringComparison.Ordinal);
        return redacted.Length <= 240 ? redacted : redacted[..240] + "…";
    }
}
