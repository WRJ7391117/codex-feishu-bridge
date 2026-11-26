using System.Collections.Concurrent;
using DeepOriBridge.Models;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;

namespace DeepOriBridge.Services;

internal sealed record PromLightDiscoveredDevice(string Id, string Label);

internal sealed class PromLightFleetService : IPromLightService, IPromLightFleet, IAsyncDisposable
{
    private readonly FeishuCredentialService _credentials;
    private readonly BridgeRuntime _runtime;
    private readonly PromLightRegistry _registry;
    private readonly string _registryPath;
    private readonly PromLightTurnOrigins _origins;
    private readonly ConcurrentDictionary<string, PromLightService> _workers = new();
    private readonly object _workersGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CodexRolloutTaskStateReader _reader = new();
    private Task? _maintenance;
    private string? _selectedId;
    private string? _selectedOwner;

    internal PromLightFleetService(FeishuCredentialService credentials, BridgeRuntime runtime)
    {
        _credentials = credentials;
        _runtime = runtime;
        string storageScope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(credentials.ResourcePrefix)));
        _registryPath = Path.Combine(BridgeLocalStorage.Root, "PromLight", storageScope, "registry-v2.bin");
        _registry = new(_registryPath);
        _origins = new(Path.Combine(Path.GetDirectoryName(_registryPath)!, "turn-origins.bin"));
    }

    private string Generation => _credentials.Load().IdentityGeneration;
    internal string ActiveOwner => _selectedOwner ?? _credentials.ActiveBoundOwnerId() ?? "";
    internal void SelectOwner(string owner) { Authorize(owner); _selectedOwner = owner; _selectedId = null; }
    public PromLightLamp[] Owned(string owner)
    {
        Authorize(owner);
        return _registry.Owned(owner, Generation);
    }
    public PromLightLamp Require(string owner, string id, long? revision = null)
    {
        Authorize(owner);
        return _registry.Require(owner, Generation, id, revision);
    }
    private void Authorize(string owner)
    {
        if (!_credentials.MatchesBoundOwner(owner)) throw new InvalidOperationException("使用者未获授权；未操作提示灯。");
    }
    internal PromLightLamp? Selected => string.IsNullOrEmpty(ActiveOwner) ? null
        : Owned(ActiveOwner).FirstOrDefault(l => l.Id == _selectedId && !l.PendingUnbind)
            ?? Owned(ActiveOwner).FirstOrDefault(l => l.IsDefault && !l.PendingUnbind);
    internal void Select(string id) { Require(ActiveOwner, id); _selectedId = id; }
    private IPromLightService SelectedService => Selected is { } lamp ? ForLamp(lamp.Owner, lamp.Id)
        : throw new InvalidOperationException("请先明确选择设备并绑定给当前使用者。");

    internal void StartMonitoring(BridgeRuntime runtime)
    {
        _maintenance ??= Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    TryMigrateSingleOwnerLegacy();
                    foreach (PromLightLamp lamp in _registry.All())
                    {
                        if (!lamp.PendingUnbind && (lamp.Generation != Generation || !_credentials.MatchesBoundOwner(lamp.Owner)))
                        {
                            // Credentials unavailable is not proof of revocation.
                            if (!FirstConnectionNavigationGate.IsCurrentlyVerified(_credentials.Load())
                                || _credentials.LoadOwnerBinding().Error is not null) continue;
                            _registry.Update(lamp.Owner, lamp.Generation, lamp.Id, lamp.Revision, unbind: true);
                        }
                        Worker(lamp);
                    }
                    foreach (string id in _workers.Keys)
                        if (!_registry.All().Any(l => l.Id == id) && _workers.TryRemove(id, out var removed))
                            await removed.DisposeAsync();
                }
                catch (Exception) { /* Fail closed on unreadable registry; retry without replacing it. */ }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private void TryMigrateSingleOwnerLegacy()
    {
        // Never replace v2, even when it is empty, corrupt, or from a future version.
        if (_credentials.ResourcePrefix != FeishuCredentialService.DevelopmentIsolatedResourcePrefix
            || File.Exists(_registryPath) || !FirstConnectionNavigationGate.IsCurrentlyVerified(_credentials.Load())) return;
        var store = new PromLightSettingsStore();
        string? device = store.LoadPinnedDevice();
        string[] owners = _credentials.BoundOwnerIds();
        var configuration = store.LoadForMigration();
        if (configuration.State != PromLightLegacyState.Valid) return;
        var legacy = configuration.RequireValid();
        if (string.IsNullOrWhiteSpace(device) || owners.Length != 1 || legacy.TaskIds.Count == 0
            || legacy.TaskIds.Any(id => !TaskAllowed(owners[0], id))) return;
        // The existing exact endpoint and all subscriptions are moved atomically;
        // the original v1 settings remain untouched for recovery.
        configuration.Import(_registry, owners[0], Generation, device, id => TaskAllowed(owners[0], id));
    }

    internal async Task<PromLightDiscoveredDevice[]> DiscoverDevicesAsync(CancellationToken token)
    {
        var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(PromLightProtocol.UsagePage,
            PromLightProtocol.UsageId, PromLightProtocol.VendorId, PromLightProtocol.ProductId)).AsTask(token);
        return devices.Where(d => d.IsEnabled).Select(d => new PromLightDiscoveredDevice(d.Id,
            (string.IsNullOrWhiteSpace(d.Name) ? "PromLight" : d.Name) + " · " + DeviceLabel(d.Id))).ToArray();
    }
    private static string DeviceLabel(string id) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(id)))[..8];

    internal async Task BindAsync(string deviceId, string name, CancellationToken token)
    {
        string owner = ActiveOwner, generation = Generation;
        Authorize(owner);
        if (!(await DiscoverDevicesAsync(token)).Any(d => d.Id == deviceId))
            throw new InvalidOperationException("选择的设备已离线；未绑定其他设备。");
        Authorize(owner);
        if (generation != Generation) throw new InvalidOperationException("Bot 身份已变化；未绑定。");
        PromLightLamp lamp = _registry.Bind(owner, generation, deviceId, name);
        _selectedId = lamp.Id;
        Worker(lamp);
    }

    internal async Task ImportLegacyAsync(CancellationToken token)
    {
        string owner = ActiveOwner, generation = Generation;
        Authorize(owner);
        var store = new PromLightSettingsStore();
        string device = store.LoadPinnedDevice() ?? throw new InvalidOperationException("旧配置没有可证明的设备身份；请手动绑定所选设备。");
        var configuration = store.LoadForMigration();
        var binding = configuration.RequireValid();
        if (!(await DiscoverDevicesAsync(token)).Any(d => d.Id == device)) throw new InvalidOperationException("旧配置对应的设备未连接；不会选择其他设备。");
        if (binding.TaskIds.Any(id => !TaskAllowed(owner, id))) throw new InvalidOperationException("旧关联含未获授权的 Task；未导入，请逐项手动关联。");
        Authorize(owner);
        if (generation != Generation) throw new InvalidOperationException("Bot 身份已变化；未导入。");
        var lamp = configuration.Import(_registry, owner, generation, device, id => TaskAllowed(owner, id));
        _selectedId = lamp.Id;
        Worker(lamp);
    }

    public IPromLightService ForLamp(string owner, string id, long? revision = null) =>
        new LampAccess(this, Require(owner, id, revision));
    public void RecordTurnOrigin(string owner, string taskId, string turnId)
    {
        Authorize(owner);
        _origins.Record(new(Generation, owner, taskId, turnId, DateTimeOffset.UtcNow));
    }
    public void Rename(string owner, string id, long revision, string name)
    {
        PromLightLamp lamp = Require(owner, id, revision);
        _registry.Update(owner, lamp.Generation, id, revision, name: name);
    }
    public void MakeDefault(string owner, string id, long revision)
    {
        PromLightLamp lamp = Require(owner, id, revision);
        _registry.Update(owner, lamp.Generation, id, revision, makeDefault: true);
    }
    public async Task UnbindAsync(string owner, string id, long revision, CancellationToken token)
    {
        PromLightLamp lamp = Require(owner, id, revision);
        lamp = _registry.Update(owner, lamp.Generation, id, revision, unbind: true);
        // Offline cleanup remains durable; only a matching idle write ACK removes it.
        await Worker(lamp).RefreshAsync(token);
    }

    private PromLightService Worker(PromLightLamp lamp)
    {
        lock (_workersGate)
        {
            if (_workers.TryGetValue(lamp.Id, out var existing)) return existing;
            PromLightLamp? Read() => _registry.All().SingleOrDefault(l => l.Id == lamp.Id);
            bool MayControl()
            {
                PromLightLamp? live = Read();
                return live is not null && (live.PendingUnbind || (live.Generation == Generation && _credentials.MatchesBoundOwner(live.Owner)));
            }
            var worker = new PromLightService(lamp, Read, (name, tasks) =>
            {
                PromLightLamp live = Require(lamp.Owner, lamp.Id);
                return _registry.Update(live.Owner, live.Generation, live.Id, live.Revision, name: name, taskIds: tasks.ToArray()).Binding;
            }, taskId => TaskAllowed(lamp.Owner, taskId), MayControl, (revision, result) =>
            {
                _registry.CompleteUnbind(lamp.Id, lamp.DeviceId, revision, result.Status, result.BytesWritten);
            }, (taskId, status) =>
            {
                if (status != PromLightStatus.HumanGate) return status;
                string turnId = _reader.ReadDesktopSyncSnapshot(taskId).TurnId;
                string? owner = _origins.Find(lamp.Generation, taskId, turnId);
                int owners = _registry.All().Where(l => l.Generation == lamp.Generation && !l.PendingUnbind && l.TaskIds.Contains(taskId))
                    .Select(l => l.Owner).Distinct().Count();
                return PromLightOwnerStatusPolicy.Resolve(status, lamp.Owner, owner, owners);
            }, token => _registry.AcquireWriteLeaseAsync(lamp.Id, token));
            _workers[lamp.Id] = worker;
            worker.StartMonitoring(_runtime);
            return worker;
        }
    }

    private bool TaskAllowed(string owner, string taskId)
    {
        if (!_credentials.MatchesBoundOwner(owner)) return false;
        FeishuProjectAuthorizationState scope = _credentials.LoadProjectAuthorization(owner);
        if (scope.Error is not null) throw new IOException("无法核实提示灯项目授权；保留原关联。");
        if (!scope.IsAuthorized) return false;
        string? path = _reader.ResolvePath(taskId);
        if (path is null)
        {
            if (_reader.ResolvePath(taskId, includeArchived: true) is not null) return false;
            throw new IOException("Task 记录暂不可读；保留原关联，稍后重试。");
        }
        // Membership is established from the actual session metadata, not its filename.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var line = new System.Text.StringBuilder();
        int character;
        while ((character = reader.Read()) >= 0 && character != '\n' && line.Length <= 64 * 1024) line.Append((char)character);
        if (line.Length == 0 || line.Length > 64 * 1024) return false;
        string first = line.ToString();
        using var json = System.Text.Json.JsonDocument.Parse(first);
        return json.RootElement.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == System.Text.Json.JsonValueKind.String
            && AuthorizedProjectScope.Contains(scope.ProjectPath, cwd.GetString());
    }

    private sealed class LampAccess(PromLightFleetService fleet, PromLightLamp captured) : IPromLightService
    {
        private PromLightLamp Check(bool mutation = false) => fleet.Require(captured.Owner, captured.Id, mutation ? captured.Revision : null);
        private PromLightService Worker => fleet.Worker(Check());
        public PromLightActivitySnapshot Activity => Worker.Activity;
        public PromLightBindingSnapshot Binding => Check().Binding;
        public Task<PromLightDeviceSnapshot> DiscoverAsync(CancellationToken token) => Worker.DiscoverAsync(token);
        public Task<PromLightWriteResult> SendAsync(PromLightStatus status, CancellationToken token) { Check(true); return Worker.SendAsync(status, token); }
        public bool SetTaskAssociation(string task, bool enabled)
        {
            var lamp = Check(true);
            if (enabled && !fleet.TaskAllowed(lamp.Owner, task)) throw new InvalidOperationException("Task 不属于该使用者授权项目。");
            var tasks = lamp.TaskIds.ToHashSet(StringComparer.Ordinal);
            bool changed = enabled ? tasks.Add(task) : tasks.Remove(task);
            if (changed) fleet._registry.Update(lamp.Owner, lamp.Generation, lamp.Id, captured.Revision, taskIds: tasks.ToArray());
            return changed;
        }
        public Task ClearTaskAssociationsAsync(CancellationToken token)
        {
            var lamp = Check(true);
            fleet._registry.Update(lamp.Owner, lamp.Generation, lamp.Id, captured.Revision, taskIds: []);
            return Worker.RefreshAsync(token);
        }
        public Task RefreshAsync(CancellationToken token) => Worker.RefreshAsync(token);
        public Task SetTransientStatusAsync(PromLightStatus status, CancellationToken token) { Check(true); return Worker.SetTransientStatusAsync(status, token); }
    }

    public PromLightActivitySnapshot Activity => Selected is null ? PromLightActivitySnapshot.Initial : SelectedService.Activity;
    public PromLightBindingSnapshot Binding => Selected?.Binding ?? PromLightBindingSnapshot.Initial;
    public async Task<PromLightDeviceSnapshot> DiscoverAsync(CancellationToken token)
    {
        if (Selected is not null) return await SelectedService.DiscoverAsync(token);
        int count = (await DiscoverDevicesAsync(token)).Length;
        return new(count > 0, false, count, "PromLight", "", 0, "请在设备列表中选择并绑定提示灯。旧单灯关联保留在原配置中，不会自动分配给使用者。", DateTimeOffset.Now);
    }
    public Task<PromLightWriteResult> SendAsync(PromLightStatus status, CancellationToken token) => SelectedService.SendAsync(status, token);
    public bool SetTaskAssociation(string task, bool enabled) => SelectedService.SetTaskAssociation(task, enabled);
    public Task ClearTaskAssociationsAsync(CancellationToken token) => SelectedService.ClearTaskAssociationsAsync(token);
    public Task RefreshAsync(CancellationToken token) => SelectedService.RefreshAsync(token);
    // Transport events have no explicit lamp owner/target: never broadcast them.
    public Task SetTransientStatusAsync(PromLightStatus status, CancellationToken token) => Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_maintenance is not null) await _maintenance;
        foreach (var worker in _workers.Values) await worker.DisposeAsync();
        _lifetime.Dispose();
    }
}
