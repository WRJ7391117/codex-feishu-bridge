namespace DeepOriBridge.Models;

internal sealed record CodexEnvironmentSnapshot(
    string WindowsVersion,
    string CodexExecutablePath,
    string CodexVersion,
    string StateDatabasePath,
    bool StateDatabaseIsValid,
    string CatalogDatabasePath,
    bool CatalogDatabaseIsValid,
    bool PrivatePipeDetected,
    bool AppServerInitialized,
    string AppServerPlatform,
    bool ThreadListSucceeded,
    int ReturnedThreadCount,
    string? Error,
    DateTimeOffset CheckedAt);
