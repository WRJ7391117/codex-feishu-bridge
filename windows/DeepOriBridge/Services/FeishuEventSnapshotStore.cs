using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class FeishuEventSnapshotStore(
    FeishuEventRuntimeSnapshot initial,
    Func<FeishuEventRuntimeSnapshot, FeishuEventRuntimeSnapshot> projector)
{
    private readonly object _sync = new();
    private FeishuEventRuntimeSnapshot _snapshot = projector(initial);

    internal FeishuEventRuntimeSnapshot Read()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    internal FeishuEventRuntimeSnapshot Replace(FeishuEventRuntimeSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = projector(snapshot);
            return _snapshot;
        }
    }

    internal FeishuEventRuntimeSnapshot Update(
        Func<FeishuEventRuntimeSnapshot, FeishuEventRuntimeSnapshot> update)
    {
        lock (_sync)
        {
            _snapshot = projector(update(_snapshot));
            return _snapshot;
        }
    }
}
