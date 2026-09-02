struct BotSetupFlow {
    private(set) var shouldContinueToConnectionSetup = false
    private(set) var isEditingCredentials = false
    private var verifiedBeforePresentation = false
    private var credentialWriteSucceeded = false

    mutating func present(
        continueToConnectionSetup: Bool,
        currentlyVerified: Bool
    ) {
        shouldContinueToConnectionSetup = continueToConnectionSetup
        isEditingCredentials = false
        verifiedBeforePresentation = currentlyVerified
        credentialWriteSucceeded = false
    }

    mutating func beginCredentialReconfiguration() {
        isEditingCredentials = true
        credentialWriteSucceeded = false
    }

    mutating func recordCredentialWriteSucceeded() {
        credentialWriteSucceeded = true
    }

    mutating func cancel(currentlyVerified: Bool) -> Bool {
        let shouldRestorePreviousVerification = isEditingCredentials
            && !credentialWriteSucceeded
        let verified = shouldRestorePreviousVerification
            ? verifiedBeforePresentation
            : currentlyVerified
        reset()
        return verified
    }

    mutating func complete(currentlyVerified: Bool) -> Bool {
        guard currentlyVerified else { return false }
        let shouldContinue = shouldContinueToConnectionSetup
        reset()
        return shouldContinue
    }

    private mutating func reset() {
        shouldContinueToConnectionSetup = false
        isEditingCredentials = false
        verifiedBeforePresentation = false
        credentialWriteSucceeded = false
    }
}
