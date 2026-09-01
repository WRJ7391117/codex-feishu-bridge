using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DeepOriBridge.Models;
using Windows.Security.Credentials;

namespace DeepOriBridge.Services;

public sealed class FeishuCredentialService
{
    private const string CredentialResource = "DeepOriBridge.Feishu.TestBot";
    private const string OwnerCredentialResource = "DeepOriBridge.Feishu.TestBot.Owner";
    private const string TokenEndpoint =
        "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly PasswordVault _vault = new();

    public FeishuStoredCredential Load()
    {
        try
        {
            PasswordCredential? credential = _vault
                .FindAllByResource(CredentialResource)
                .FirstOrDefault();
            if (credential is null)
            {
                return new FeishuStoredCredential(string.Empty, false);
            }

            credential.RetrievePassword();
            return new FeishuStoredCredential(
                credential.UserName,
                !string.IsNullOrEmpty(credential.Password));
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
        string normalizedAppId = appId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAppId))
        {
            return new FeishuVerificationResult(false, "请输入 App ID。");
        }

        string secret = suppliedSecret;
        if (string.IsNullOrEmpty(secret))
        {
            secret = RetrieveSavedSecret(normalizedAppId) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(secret))
        {
            return new FeishuVerificationResult(false, "请输入 App Secret；它不会显示在页面或日志中。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = JsonContent.Create(new
                {
                    app_id = normalizedAppId,
                    app_secret = secret,
                }),
            };
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);

            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int code = root.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.TryGetInt32(out int parsedCode)
                    ? parsedCode
                    : -1;
            string message = root.TryGetProperty("msg", out JsonElement messageElement)
                ? messageElement.GetString() ?? "飞书没有返回错误说明。"
                : "飞书没有返回错误说明。";
            bool tokenReturned = root.TryGetProperty("tenant_access_token", out JsonElement tokenElement)
                && !string.IsNullOrWhiteSpace(tokenElement.GetString());

            if (!response.IsSuccessStatusCode || code != 0 || !tokenReturned)
            {
                return new FeishuVerificationResult(
                    false,
                    $"飞书凭证验证失败（code {code}）：{Redact(message, normalizedAppId, secret)}");
            }

            int? expiresIn = root.TryGetProperty("expire", out JsonElement expireElement)
                && expireElement.TryGetInt32(out int parsedExpire)
                    ? parsedExpire
                    : null;

            Save(normalizedAppId, secret);
            return new FeishuVerificationResult(
                true,
                "凭证有效；飞书已签发临时 tenant token。Token 未保存、未显示。",
                expiresIn);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return new FeishuVerificationResult(
                false,
                $"无法连接飞书开放平台：{exception.GetType().Name}");
        }
        catch (JsonException)
        {
            return new FeishuVerificationResult(false, "飞书返回了无法识别的验证响应。");
        }
        finally
        {
            secret = string.Empty;
        }
    }

    public void Clear()
    {
        foreach (PasswordCredential credential in FindCredentials())
        {
            _vault.Remove(credential);
        }

        ClearOwnerBinding();
    }

    public FeishuOwnerBindingState LoadOwnerBinding()
    {
        FeishuStoredCredential bot = Load();
        if (string.IsNullOrWhiteSpace(bot.AppId))
        {
            return new FeishuOwnerBindingState(false);
        }

        try
        {
            PasswordCredential? credential = FindOwnerCredentials()
                .FirstOrDefault(item => item.UserName.Equals(bot.AppId, StringComparison.Ordinal));
            if (credential is null)
            {
                return new FeishuOwnerBindingState(false);
            }

            credential.RetrievePassword();
            return new FeishuOwnerBindingState(!string.IsNullOrWhiteSpace(credential.Password));
        }
        catch (Exception exception)
        {
            return new FeishuOwnerBindingState(
                false,
                $"无法读取 owner 白名单：{exception.GetType().Name}");
        }
    }

    public void BindOwner(string senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            throw new InvalidOperationException("飞书事件缺少可绑定的 sender ID。");
        }

        FeishuStoredCredential bot = Load();
        if (!bot.HasSecret || string.IsNullOrWhiteSpace(bot.AppId))
        {
            throw new InvalidOperationException("请先保存并验证飞书测试机器人凭证。");
        }

        ClearOwnerBinding();
        _vault.Add(new PasswordCredential(
            OwnerCredentialResource,
            bot.AppId,
            senderId));
    }

    public void ClearOwnerBinding()
    {
        foreach (PasswordCredential credential in FindOwnerCredentials())
        {
            _vault.Remove(credential);
        }
    }

    internal bool MatchesBoundOwner(string senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            return false;
        }

        FeishuStoredCredential bot = Load();
        PasswordCredential? credential = FindOwnerCredentials()
            .FirstOrDefault(item => item.UserName.Equals(bot.AppId, StringComparison.Ordinal));
        if (credential is null)
        {
            return false;
        }

        credential.RetrievePassword();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(credential.Password),
            System.Text.Encoding.UTF8.GetBytes(senderId));
    }

    internal (string AppId, string Secret)? LoadForRuntime()
    {
        FeishuStoredCredential stored = Load();
        if (!stored.HasSecret || string.IsNullOrWhiteSpace(stored.AppId))
        {
            return null;
        }

        string? secret = RetrieveSavedSecret(stored.AppId);
        return string.IsNullOrEmpty(secret) ? null : (stored.AppId, secret);
    }

    private void Save(string appId, string secret)
    {
        string previousAppId = Load().AppId;
        foreach (PasswordCredential credential in FindCredentials())
        {
            _vault.Remove(credential);
        }

        if (!string.IsNullOrWhiteSpace(previousAppId)
            && !previousAppId.Equals(appId, StringComparison.Ordinal))
        {
            ClearOwnerBinding();
        }

        _vault.Add(new PasswordCredential(CredentialResource, appId, secret));
    }

    private string? RetrieveSavedSecret(string appId)
    {
        PasswordCredential? credential = FindCredentials()
            .FirstOrDefault(item => item.UserName.Equals(appId, StringComparison.Ordinal));
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
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
