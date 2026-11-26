using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal interface IPromLightFleet
{
    PromLightLamp[] Owned(string owner);
    PromLightLamp Require(string owner, string id, long? revision = null);
    IPromLightService ForLamp(string owner, string id, long? revision = null);
    void Rename(string owner, string id, long revision, string name);
    void MakeDefault(string owner, string id, long revision);
    Task UnbindAsync(string owner, string id, long revision, CancellationToken token);
    void RecordTurnOrigin(string owner, string taskId, string turnId);
}

internal interface IPromLightService
{
    PromLightActivitySnapshot Activity { get; }

    PromLightBindingSnapshot Binding { get; }

    Task<PromLightDeviceSnapshot> DiscoverAsync(CancellationToken cancellationToken);

    Task<PromLightWriteResult> SendAsync(
        PromLightStatus status,
        CancellationToken cancellationToken);

    bool SetTaskAssociation(string taskId, bool enabled);

    Task ClearTaskAssociationsAsync(CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);

    Task SetTransientStatusAsync(
        PromLightStatus status,
        CancellationToken cancellationToken);
}
