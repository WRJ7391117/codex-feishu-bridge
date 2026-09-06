using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal enum FirstConnectionDestination
{
    BotCredentialPreparation,
    FirstConnectionAssistant,
}

internal static class FirstConnectionNavigationGate
{
    internal const string BotCredentialFocusParameter = "bot-credential-gate";

    internal static bool IsCurrentlyVerified(FeishuStoredCredential credential) =>
        credential.HasSecret
        && credential.HasVerifiedIdentity
        && credential.IdentityGeneration.Length == 32
        && string.IsNullOrWhiteSpace(credential.Error);

    internal static FirstConnectionDestination Resolve(FeishuStoredCredential credential) =>
        IsCurrentlyVerified(credential)
            ? FirstConnectionDestination.FirstConnectionAssistant
            : FirstConnectionDestination.BotCredentialPreparation;
}

internal sealed class BotSetupTransaction
{
    internal BotSetupTransaction(FeishuStoredCredential beforePresentation)
    {
        VerifiedBeforePresentation = FirstConnectionNavigationGate.IsCurrentlyVerified(
            beforePresentation);
        IdentityGenerationBeforePresentation = beforePresentation.IdentityGeneration;
        CurrentlyVerified = VerifiedBeforePresentation;
    }

    internal bool VerifiedBeforePresentation { get; }

    internal string IdentityGenerationBeforePresentation { get; }

    internal bool CredentialWriteSucceeded { get; private set; }

    internal bool CurrentlyVerified { get; private set; }

    internal void ObserveVerification(
        FeishuVerificationResult result,
        FeishuStoredCredential storedAfterAttempt)
    {
        CredentialWriteSucceeded |= result.CredentialWriteSucceeded;
        CurrentlyVerified = FirstConnectionNavigationGate.IsCurrentlyVerified(
            storedAfterAttempt);
    }

    internal bool CancelMayRestorePriorPresentation(FeishuStoredCredential storedNow) =>
        !CredentialWriteSucceeded
        && VerifiedBeforePresentation
        && storedNow.IdentityGeneration.Equals(
            IdentityGenerationBeforePresentation,
            StringComparison.Ordinal)
        && FirstConnectionNavigationGate.IsCurrentlyVerified(storedNow);
}

internal static class SetupCompletionGatePolicy
{
    internal static FeishuMenuConfigurationStage ObserveMenuReply(
        FeishuMenuConfigurationStage stage, bool sameIdentity, bool ownerAuthorized,
        bool knownEvent, bool replySucceeded) =>
        stage == FeishuMenuConfigurationStage.PublishedPendingClientSync
        && sameIdentity && ownerAuthorized && knownEvent && replySucceeded
            ? FeishuMenuConfigurationStage.VisibleVerified : stage;

    internal static bool CanCompleteAutomatic(
        bool localGatesComplete, FeishuMenuConfigurationStage menuStage) =>
        localGatesComplete && menuStage == FeishuMenuConfigurationStage.VisibleVerified;

    internal static bool CanComplete(
        bool currentlyVerified,
        bool ownerBound,
        bool projectAuthorized,
        int registeredConsumers,
        int expectedConsumers,
        int receivedTextEvents,
        int successfulTextReplies) =>
        currentlyVerified
        && ownerBound
        && projectAuthorized
        && expectedConsumers == FeishuSidecarProtocol.ExpectedConsumers.Length
        && registeredConsumers == expectedConsumers
        && receivedTextEvents > 0
        && successfulTextReplies > 0;
}
