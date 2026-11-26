using System.Security.Cryptography;
using System.Text;

namespace DeepOriBridge.Services;

internal static class FeishuAppIdentityPolicy
{
    private const string FingerprintVersion = "DeepOriBridge.FeishuIdentity.v1";

    internal static string Fingerprint(
        string appId,
        string botOpenId,
        string botDisplayName) => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\0',
                FingerprintVersion,
                appId.Trim(),
                botOpenId.Trim(),
                botDisplayName.Trim()))));

    internal static bool Matches(
        string appId,
        string botOpenId,
        string botDisplayName,
        string persistedFingerprint) =>
        appId.Length > 0
        && botOpenId.Length > 0
        && botDisplayName.Length > 0
        && persistedFingerprint.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Fingerprint(appId, botOpenId, botDisplayName)),
            Encoding.UTF8.GetBytes(persistedFingerprint));

    internal static bool MayStartSidecar(
        string appId,
        string botOpenId,
        string botDisplayName,
        bool hasSecret,
        bool hasVerifiedIdentity,
        string persistedFingerprint) =>
        hasSecret
        && hasVerifiedIdentity
        && Matches(appId, botOpenId, botDisplayName, persistedFingerprint);
}
