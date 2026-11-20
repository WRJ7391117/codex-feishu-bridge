using System.Globalization;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DeepOriBridge.Models;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeepOriBridge.Services;

internal sealed partial class PromLightService : IPromLightService, IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _statusUpdateLock = new(1, 1);
    private readonly object _bindingLock = new();
    private readonly PromLightSettingsStore _settingsStore = new();
    private readonly CodexRolloutTaskStateReader _rolloutReader = new();
    private readonly CodexDesktopApprovalObserver _approvalObserver = new();
    private readonly ConcurrentDictionary<string, bool> _waitingOnApproval =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _monitorLifetime = new();
    private HidDevice? _device;
    private string? _pinnedDeviceId;
    private PromLightActivitySnapshot _activity = PromLightActivitySnapshot.Initial;
    private PromLightBindingSnapshot _binding;
    private BridgeRuntime? _runtime;
    private Task? _monitorTask;
    private Task? _approvalTask;
    private sealed record TaskAggregate(PromLightStatus Status, long? Revision, bool HasTasks);
    private TaskAggregate _taskAggregate = new(PromLightStatus.Idle, null, false);
    private PromLightTransientSignal? _transientSignal;
    private readonly object _diagnosticLock = new();
    private readonly Func<PromLightLamp?>? _readLamp;
    private readonly Func<string, IEnumerable<string>, PromLightBindingSnapshot>? _saveBinding;
    private readonly Func<string, bool>? _taskAllowed;
    private readonly Func<bool>? _mayControl;
    private readonly Action<long, PromLightWriteResult>? _written;
    private readonly Func<string, PromLightStatus, PromLightStatus>? _ownerStatus;
    private readonly PromLightRetryPolicy _retry = new();
    private readonly Func<CancellationToken, Task<IDisposable>>? _acquireWriteLease;

    private void Trace(string stage, object details)
    {
        // Local, bounded diagnostics: IDs and state only; never prompts or credentials.
        try
        {
            lock (_diagnosticLock)
            {
                string path = Path.Combine(BridgeLocalStorage.Root, "promlight-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow, stage, details,
                }) + "\n");
            }
        }
        catch { /* Diagnostics must not prevent lamp updates. */ }
    }

    internal PromLightService()
    {
        _binding = _settingsStore.Load();
        _pinnedDeviceId = _settingsStore.LoadPinnedDevice();
    }

    internal PromLightService(PromLightLamp lamp, Func<PromLightLamp?> readLamp,
        Func<string, IEnumerable<string>, PromLightBindingSnapshot> saveBinding,
        Func<string, bool> taskAllowed, Func<bool> mayControl, Action<long, PromLightWriteResult> written,
        Func<string, PromLightStatus, PromLightStatus>? ownerStatus = null,
        Func<CancellationToken, Task<IDisposable>>? acquireWriteLease = null)
    {
        _binding = lamp.Binding;
        _pinnedDeviceId = lamp.DeviceId;
        _readLamp = readLamp;
        _saveBinding = saveBinding;
        _taskAllowed = taskAllowed;
        _mayControl = mayControl;
        _written = written;
        _ownerStatus = ownerStatus;
        _acquireWriteLease = acquireWriteLease;
    }

    private void EnsureControlAllowed()
    {
        if (_mayControl is not null && !_mayControl())
            throw new InvalidOperationException("提示灯绑定或使用者授权已失效；未控制设备。");
    }

    private PromLightBindingSnapshot SaveBinding(string name, IEnumerable<string> tasks) =>
        _saveBinding is not null ? _saveBinding(name, tasks) : _settingsStore.Save(name, tasks);

    public PromLightActivitySnapshot Activity => Volatile.Read(ref _activity);

    public PromLightBindingSnapshot Binding
    {
        get
        {
            lock (_bindingLock)
            {
                return _readLamp is not null ? _readLamp()?.Binding ?? PromLightBindingSnapshot.Initial
                    : _binding with { TaskIds = _binding.TaskIds.ToArray() };
            }
        }
    }

    internal void StartMonitoring(BridgeRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (Interlocked.CompareExchange(ref _runtime, runtime, null) is not null)
        {
            return;
        }

        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorLifetime.Token));
        _approvalTask = Task.Run(() => _approvalObserver.ObserveAsync(
            () => Binding.TaskIds,
            OnApprovalWaitingChanged,
            _monitorLifetime.Token));
    }

    public bool SetTaskAssociation(string taskId, bool enabled)
    {
        EnsureControlAllowed();
        if (enabled && _taskAllowed is not null && !_taskAllowed(taskId))
            throw new InvalidOperationException("Task 不在这盏灯使用者的授权范围内。");
        if (!IsSafeTaskId(taskId))
        {
            throw new ArgumentException("PromLight Task 引用无效。", nameof(taskId));
        }

        bool changed;
        lock (_bindingLock)
        {
            var ids = Binding.TaskIds.ToHashSet(StringComparer.Ordinal);
            changed = enabled ? ids.Add(taskId) : ids.Remove(taskId);
            if (changed)
            {
                _binding = SaveBinding(Binding.Name, ids);
            }
        }
        if (changed)
        {
            if (!enabled)
            {
                _waitingOnApproval.TryRemove(taskId, out _);
            }
        }
        return changed;
    }

    public async Task ClearTaskAssociationsAsync(CancellationToken cancellationToken)
    {
        lock (_bindingLock)
        {
            _binding = SaveBinding(Binding.Name, Array.Empty<string>());
        }
        _waitingOnApproval.Clear();
        Volatile.Write(ref _taskAggregate, new(PromLightStatus.Idle, _readLamp?.Invoke()?.Revision, false));
        Volatile.Write(ref _transientSignal, null);
        await DeliverEffectiveStatusAsync(force: true, cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await EvaluateAssociatedTasksAsync(force: true, cancellationToken);
    }

    public async Task SetTransientStatusAsync(
        PromLightStatus status,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _transientSignal, PromLightTransientSignal.Create(status, DateTimeOffset.UtcNow));
        await DeliverEffectiveStatusAsync(force: true, cancellationToken);
    }

    public async Task<PromLightDeviceSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureControlAllowed();
            _device?.Dispose();
            _device = null;

            string selector = HidDevice.GetDeviceSelector(
                PromLightProtocol.UsagePage,
                PromLightProtocol.UsageId,
                PromLightProtocol.VendorId,
                PromLightProtocol.ProductId);
            string[] requestedProperties = ["System.Devices.HardwareIds"];
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                    selector,
                    requestedProperties)
                .AsTask(cancellationToken);
            if (devices.Count == 0)
            {
                _device = null;
                Volatile.Write(ref _activity, PromLightActivitySnapshot.Initial);
                return new PromLightDeviceSnapshot(
                    false,
                    false,
                    0,
                    string.Empty,
                    string.Empty,
                    0,
                    "未发现匹配 FF00:0001 的 PromLight HID 接口。",
                    DateTimeOffset.Now);
            }

            string? selectedId = PromLightDeviceSelectionPolicy.Select(
                devices.Where(device => device.IsEnabled).Select(device => device.Id), _pinnedDeviceId);
            if (selectedId is null)
            {
                Volatile.Write(ref _activity, PromLightActivitySnapshot.Initial);
                return new PromLightDeviceSnapshot(true, false, devices.Count, "PromLight", "", 0,
                    _pinnedDeviceId is null
                        ? "发现多盏提示灯，当前单灯绑定无法确定目标；已停止自动选灯和写入，等待按设备明确绑定。"
                        : "原提示灯不在线；已保留绑定，不会切换到其他提示灯。",
                    DateTimeOffset.Now);
            }
            DeviceInformation selected = devices.Single(device => device.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            if (_pinnedDeviceId is null) _settingsStore.PinDevice(selected.Id);
            _pinnedDeviceId = selected.Id;
            int releaseNumber = ParseReleaseNumber(selected);
            _device = await HidDevice.FromIdAsync(selected.Id, FileAccessMode.ReadWrite)
                .AsTask(cancellationToken);

            if (_device is null)
            {
                Volatile.Write(ref _activity, PromLightActivitySnapshot.Initial);
                return new PromLightDeviceSnapshot(
                    true,
                    false,
                    devices.Count,
                    "PromLight",
                    DeviceVersion(releaseNumber),
                    releaseNumber,
                    "已发现 PromLight，但 Windows 未授予 HID 读写句柄。",
                    DateTimeOffset.Now);
            }

            return new PromLightDeviceSnapshot(
                true,
                true,
                devices.Count,
                "PromLight",
                DeviceVersion(releaseNumber),
                releaseNumber,
                releaseNumber == PromLightProtocol.SupportedReleaseNumber
                    ? null
                    : $"设备 release 0x{releaseNumber:X4} 尚未经过协议验证。",
                DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Volatile.Write(ref _activity, PromLightActivitySnapshot.Initial);
            return new PromLightDeviceSnapshot(
                false,
                false,
                0,
                string.Empty,
                string.Empty,
                0,
                exception.Message,
                DateTimeOffset.Now);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<PromLightWriteResult> SendAsync(PromLightStatus status, CancellationToken cancellationToken) =>
        SendCoreAsync(status, cancellationToken, null);

    private async Task<PromLightWriteResult> SendCoreAsync(
        PromLightStatus status,
        CancellationToken cancellationToken, long? expectedRevision)
    {
        using IDisposable? writeLease = _acquireWriteLease is null ? null : await _acquireWriteLease(cancellationToken);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureControlAllowed();
            long revision = _readLamp?.Invoke()?.Revision ?? 0;
            if (expectedRevision.HasValue && revision != expectedRevision.Value)
                throw new InvalidOperationException("提示灯订阅已变化；已丢弃旧状态，等待重新计算。");
            if (_readLamp?.Invoke()?.PendingUnbind == true && status != PromLightStatus.Idle)
                throw new InvalidOperationException("提示灯正在解绑，只允许清灯写入。");
            if (_device is null)
            {
                throw new InvalidOperationException("PromLight 尚未打开。请先重新检测设备。");
            }

            byte[] frame = PromLightProtocol.CreateFrame(status);
            HidOutputReport report = _device.CreateOutputReport(PromLightProtocol.OutputReportId);
            int reportLength = checked((int)Math.Max(
                report.Data.Length,
                report.Data.Capacity));
            byte[] reportBytes;
            if (reportLength == frame.Length + 1)
            {
                reportBytes = new byte[reportLength];
                reportBytes[0] = checked((byte)report.Id);
                frame.CopyTo(reportBytes, 1);
            }
            else if (reportLength == frame.Length)
            {
                reportBytes = frame;
            }
            else
            {
                throw new InvalidOperationException(
                    $"PromLight HID 输出报告长度为 {reportLength}，预期为 64 或 65；已拒绝写入。");
            }

            using var writer = new DataWriter();
            writer.WriteBytes(reportBytes);
            report.Data = writer.DetachBuffer();
            EnsureControlAllowed();
            if (_readLamp is not null && _readLamp()?.Revision != revision)
                throw new InvalidOperationException("提示灯配置已变化；未发送陈旧状态。");
            cancellationToken.ThrowIfCancellationRequested();
            // Once submitted, retain the registry lease until the OS operation
            // really completes. Cancelling only its waiter could permit a late
            // frame after an apparently successful unbind.
            uint bytesWritten = await _device.SendOutputReportAsync(report).AsTask();
            if (bytesWritten == 0)
            {
                throw new IOException("Windows HID 返回 0 字节写入。");
            }

            var result = new PromLightWriteResult(status, bytesWritten, DateTimeOffset.Now);
            _written?.Invoke(revision, result);
            Volatile.Write(
                ref _activity,
                new PromLightActivitySnapshot(
                    result.Status,
                    result.BytesWritten,
                    result.SubmittedAt));
            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                await EvaluateAssociatedTasksAsync(force: false, deadline.Token);
                // Expiry and retries must run even when Task state did not change.
                await DeliverEffectiveStatusAsync(force: false, deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A light indicator must never fault the Codex or Feishu bridge runtime.
                Trace("monitor-error", new { error = exception.GetType().Name, exception.HResult });
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task EvaluateAssociatedTasksAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> taskIds = Binding.TaskIds;
        foreach (string taskId in taskIds)
            if ((_taskAllowed is not null && !_taskAllowed(taskId)) || (_rolloutReader.ResolvePath(taskId) is null
                && _rolloutReader.ResolvePath(taskId, includeArchived: true) is not null))
                SetTaskAssociation(taskId, false);
        PromLightLamp? evaluatedLamp = _readLamp?.Invoke();
        taskIds = evaluatedLamp?.TaskIds ?? Binding.TaskIds;
        Trace("evaluate", new { taskCount = taskIds.Count, runtime = _runtime?.Snapshot.State.ToString() });
        if (taskIds.Count == 0)
        {
            Volatile.Write(ref _taskAggregate, new(PromLightStatus.Idle, evaluatedLamp?.Revision, false));
            if (force)
            {
                await DeliverEffectiveStatusAsync(force: true, cancellationToken);
            }
            return;
        }

        BridgeRuntime? runtime = Volatile.Read(ref _runtime);
        if (runtime is null || runtime.Snapshot.State != BridgeRuntimeState.Running)
        {
            return;
        }

        IReadOnlyList<CodexThreadSummary> threads =
            await runtime.GetRecentThreadsAsync(cancellationToken);
        Trace("threads-read", new { count = threads.Count });
        Dictionary<string, CodexThreadSummary> visible = threads
            .Where(thread => taskIds.Contains(thread.ThreadId, StringComparer.Ordinal))
            .ToDictionary(thread => thread.ThreadId, StringComparer.Ordinal);
        var statuses = new List<PromLightStatus>(taskIds.Count);
        foreach (string taskId in taskIds)
        {
            PromLightStatus rollout = _rolloutReader.ReadLatestStatus(taskId);
            bool waiting = _waitingOnApproval.TryGetValue(taskId, out bool approval)
                && approval;
            PromLightStatus resolved = visible.TryGetValue(taskId, out CodexThreadSummary? thread)
                ? PromLightTaskStatusPolicy.Resolve(thread, rollout, waiting)
                : waiting ? PromLightStatus.HumanGate : rollout;
            statuses.Add(_ownerStatus is null ? resolved : _ownerStatus(taskId, resolved));
            Trace("task-state", new { rollout = rollout.ToString(), waiting,
                summary = thread?.Status, resolved = resolved.ToString() });
        }

        PromLightStatus aggregate = PromLightTaskStatusPolicy.Aggregate(statuses);
        bool changed = aggregate != Volatile.Read(ref _taskAggregate).Status;
        Volatile.Write(ref _taskAggregate, new(aggregate, evaluatedLamp?.Revision, true));
        if (changed || force || Activity.SubmittedAt is null)
        {
            await DeliverEffectiveStatusAsync(force || changed, cancellationToken);
        }
    }

    private async Task DeliverEffectiveStatusAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        await _statusUpdateLock.WaitAsync(cancellationToken);
        try
        {
            TaskAggregate aggregate = Volatile.Read(ref _taskAggregate);
            long? desiredRevision = aggregate.Revision;
            if (_readLamp is not null && (desiredRevision is null || _readLamp()?.Revision != desiredRevision))
                return; // Recompute from the new subscriptions before sending anything.
            PromLightStatus desired = PromLightTransientSignal.Effective(
                aggregate.Status, aggregate.HasTasks,
                Volatile.Read(ref _transientSignal), DateTimeOffset.UtcNow);
            PromLightActivitySnapshot current = Activity;
            if (!_retry.MayAttempt(desired, DateTimeOffset.UtcNow, force)) return;
            Trace("deliver", new { desired = desired.ToString(), last = current.Status.ToString(),
                current.SubmittedAt, force });
            // A previous successful write does not prove the current BLE connection.
            // Reopen/reassert at a bounded cadence even if the Task state is unchanged.
            if (!force && !PromLightProtocol.NeedsRefresh(current, desired, DateTimeOffset.UtcNow))
            {
                return;
            }

            PromLightDeviceSnapshot device = await DiscoverAsync(cancellationToken);
            Trace("device", new { device.Found, device.Opened, device.ReleaseNumber });
            if (!device.Found
                || !device.Opened
                || device.ReleaseNumber != PromLightProtocol.SupportedReleaseNumber)
            {
                _retry.Failed(DateTimeOffset.UtcNow, Random.Shared.NextDouble());
                return;
            }
            PromLightWriteResult result = await SendCoreAsync(desired, cancellationToken, desiredRevision);
            _retry.Succeeded();
            Trace("written", new { status = result.Status.ToString(), result.BytesWritten, result.SubmittedAt });
        }
        catch
        {
            _retry.Failed(DateTimeOffset.UtcNow, Random.Shared.NextDouble());
            throw;
        }
        finally
        {
            _statusUpdateLock.Release();
        }
    }

    private void OnApprovalWaitingChanged(string taskId, bool waiting)
    {
        if (!Binding.TaskIds.Contains(taskId, StringComparer.Ordinal))
        {
            return;
        }
        if (waiting)
        {
            _waitingOnApproval[taskId] = true;
        }
        else
        {
            _waitingOnApproval.TryRemove(taskId, out _);
        }
    }

    private static bool IsSafeTaskId(string? taskId) =>
        !string.IsNullOrWhiteSpace(taskId)
        && taskId.Length <= 128
        && taskId.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');

    private static int ParseReleaseNumber(DeviceInformation device)
    {
        string source = device.Id;
        if (device.Properties.TryGetValue(
                "System.Devices.HardwareIds",
                out object? hardwareIdValue))
        {
            source += hardwareIdValue is IEnumerable<string> hardwareIds
                ? string.Join(';', hardwareIds)
                : hardwareIdValue?.ToString();
        }

        Match match = ReleaseNumberPattern().Match(source);
        return match.Success
            && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out int release)
                    ? release
                    : 0;
    }

    private static string DeviceVersion(int releaseNumber) =>
        releaseNumber == PromLightProtocol.SupportedReleaseNumber ? "0.1.3" : string.Empty;

    [GeneratedRegex(@"REV[&#_]([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNumberPattern();

    public async ValueTask DisposeAsync()
    {
        _monitorLifetime.Cancel();
        foreach (Task? task in new[] { _monitorTask, _approvalTask })
        {
            if (task is null) continue;
            try { await task; }
            catch (OperationCanceledException) { }
            catch { }
        }
        await _operationLock.WaitAsync();
        try
        {
            _device?.Dispose();
            _device = null;
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
            _statusUpdateLock.Dispose();
            _monitorLifetime.Dispose();
        }
    }
}
