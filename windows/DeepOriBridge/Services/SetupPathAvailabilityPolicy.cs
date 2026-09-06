namespace DeepOriBridge.Services;

internal readonly record struct SetupPathAvailability(
    bool ManualEnabled,
    bool CodexEnabled,
    bool ApplicationCreationGuidanceEnabled);

internal static class SetupPathAvailabilityPolicy
{
    internal static SetupPathAvailability Evaluate(
        bool storedCredentialVerified,
        string currentAppId,
        string storedAppId,
        bool verificationInProgress,
        bool productIdentityBound) => new(
            ManualEnabled: storedCredentialVerified
                && !verificationInProgress
                && !string.IsNullOrWhiteSpace(currentAppId)
                && currentAppId.Trim().Equals(
                    storedAppId,
                    StringComparison.Ordinal),
            CodexEnabled: storedCredentialVerified
                && !verificationInProgress
                && !string.IsNullOrWhiteSpace(currentAppId)
                && currentAppId.Trim().Equals(
                    storedAppId,
                    StringComparison.Ordinal)
                && productIdentityBound,
            ApplicationCreationGuidanceEnabled: true);
}
