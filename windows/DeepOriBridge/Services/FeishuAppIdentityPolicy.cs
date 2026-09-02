using System.Security.Cryptography;
using System.Text;

namespace DeepOriBridge.Services;

internal static class FeishuAppIdentityPolicy
{
    internal static string Fingerprint(string appId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId.Trim())));

    internal static bool Matches(string appId, string persistedFingerprint) =>
        appId.Length > 0
        && persistedFingerprint.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Fingerprint(appId)),
            Encoding.UTF8.GetBytes(persistedFingerprint));

    internal static bool MayStartSidecar(
        string appId,
        bool hasSecret,
        bool hasVerifiedIdentity,
        string persistedFingerprint) =>
        hasSecret
        && hasVerifiedIdentity
        && Matches(appId, persistedFingerprint);
}
