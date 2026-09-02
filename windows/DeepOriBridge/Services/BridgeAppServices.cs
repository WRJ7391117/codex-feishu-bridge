namespace DeepOriBridge.Services;

internal sealed class BridgeAppServices
{
    private Task? _shutdownTask;

    public BridgeProductIdentity ProductIdentity { get; } = new();

    public CodexEnvironmentProbe EnvironmentProbe { get; } = new();

    public BridgeRuntime Runtime { get; } = new();

    public PromLightService PromLight { get; } = new();

    public FeishuCredentialService FeishuCredentials { get; } = new();

    public FeishuEventRuntime FeishuEvents { get; }

    public CodexSetupSkillInstaller SetupSkillInstaller { get; } = new();

    public BridgeAppServices()
    {
        FeishuEvents = new FeishuEventRuntime(FeishuCredentials);
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        await FeishuEvents.DisposeAsync();
        await Runtime.StopAsync();
        await PromLight.DisposeAsync();
    }
}
