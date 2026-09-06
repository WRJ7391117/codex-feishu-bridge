using System.Reflection;

namespace DeepOriBridge.Services;

internal sealed class BridgeAppServices
{
    private const string CleanFirstRunUiTestMetadataKey =
        "DeepOriBridge.CleanFirstRunUiTestToken";
    private Task? _shutdownTask;

    public BridgeProductIdentity ProductIdentity { get; } = new();

    public BridgeSetupHandshakeService SetupHandshake { get; }

    public CodexEnvironmentProbe EnvironmentProbe { get; } = new();

    public BridgeRuntime Runtime { get; }

    public PromLightFleetService PromLight { get; }

    public FeishuCredentialService FeishuCredentials { get; }

    public FeishuEventRuntime FeishuEvents { get; }

    public CodexSetupSkillInstaller SetupSkillInstaller { get; } = new();

    public BridgeAppServices()
    {
        string? cleanFirstRunTestPrefix = ResolveCleanFirstRunUiTestPrefix();
        FeishuCredentials = cleanFirstRunTestPrefix is not null
            ? new FeishuCredentialService(cleanFirstRunTestPrefix)
            : ProductIdentity.IsDevelopmentIdentity
                ? new FeishuCredentialService(
                    FeishuCredentialService.DevelopmentIsolatedResourcePrefix)
                : new FeishuCredentialService();
        Runtime = new BridgeRuntime(CurrentTaskSelectionStore.ForApp(
            FeishuCredentials.CurrentTaskSelectionScope,
            () => FeishuCredentials.LoadProjectAuthorization() is { IsAuthorized: true } authorization
                ? authorization.ProjectPath : null));
        SetupHandshake = new BridgeSetupHandshakeService(
            ProductIdentity,
            FeishuCredentials);
        PromLight = new PromLightFleetService(FeishuCredentials, Runtime);
        FeishuEvents = new FeishuEventRuntime(
            FeishuCredentials,
            ProductIdentity,
            Runtime,
            PromLight);
        PromLight.StartMonitoring(Runtime);
    }

    private static string? ResolveCleanFirstRunUiTestPrefix()
    {
        string token = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(
                CleanFirstRunUiTestMetadataKey,
                StringComparison.Ordinal))
            ?.Value ?? string.Empty;
        return token.Length == 32 && token.All(Uri.IsHexDigit)
            ? FeishuCredentialService.CleanFirstRunUiTestResourcePrefix
                + token.ToUpperInvariant()
            : null;
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        await FeishuEvents.DisposeAsync();
        await SetupHandshake.DisposeAsync();
        await PromLight.DisposeAsync();
        await Runtime.StopAsync();
    }
}
