using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal static class FeishuIdentityGenerationPolicy
{
    internal static bool RequiresAtomicInvalidation(
        FeishuEventRuntimeState state,
        string intendedAppId,
        FeishuStoredCredential stored) =>
        !stored.HasVerifiedIdentity
        || stored.IdentityGeneration.Length != 32
        || !stored.AppId.Equals(intendedAppId.Trim(), StringComparison.Ordinal)
        || state is FeishuEventRuntimeState.Configuring
            or FeishuEventRuntimeState.Connecting
            or FeishuEventRuntimeState.Listening
            or FeishuEventRuntimeState.Stopping;

    internal static bool MayStart(
        string intendedAppId,
        FeishuStoredCredential stored) =>
        stored.HasSecret
        && stored.HasVerifiedIdentity
        && stored.IdentityGeneration.Length == 32
        && stored.AppFingerprint.Length == 12
        && stored.AppId.Equals(intendedAppId.Trim(), StringComparison.Ordinal);
}
