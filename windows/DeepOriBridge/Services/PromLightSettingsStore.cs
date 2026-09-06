using System.Text.Json;
using DeepOriBridge.Models;
using Windows.Storage;

namespace DeepOriBridge.Services;

internal sealed class PromLightSettingsStore
{
    private const string SettingsKey = "DeepOriBridge.PromLight.Configuration.v1";
    private const int MaxTaskCount = 50;
    private const string DeviceIdentityKey = "DeepOriBridge.PromLight.PinnedDevice.v1";

    internal PromLightLegacyConfiguration LoadForMigration()
    {
        try { return PromLightLegacyConfiguration.Parse(ApplicationData.Current.LocalSettings.Values[SettingsKey]); }
        catch { return new(PromLightLegacyState.Invalid, null); }
    }

    internal string? LoadPinnedDevice() =>
        ApplicationData.Current.LocalSettings.Values[DeviceIdentityKey] as string;

    internal void PinDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("提示灯设备身份不能为空。");
        ApplicationData.Current.LocalSettings.Values[DeviceIdentityKey] = deviceId;
    }

    private sealed record StoredSettings(
        int Version,
        string Name,
        string[] TaskIds,
        long UpdatedAtUnixSeconds);

    internal PromLightBindingSnapshot Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[SettingsKey] is not string json
                || string.IsNullOrWhiteSpace(json))
            {
                return PromLightBindingSnapshot.Initial;
            }

            StoredSettings? stored = JsonSerializer.Deserialize<StoredSettings>(json);
            if (stored is null || stored.Version != 1)
            {
                return PromLightBindingSnapshot.Initial;
            }

            return Normalize(
                stored.Name,
                stored.TaskIds,
                stored.UpdatedAtUnixSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(stored.UpdatedAtUnixSeconds)
                    : DateTimeOffset.MinValue);
        }
        catch
        {
            return PromLightBindingSnapshot.Initial;
        }
    }

    internal PromLightBindingSnapshot Save(string name, IEnumerable<string> taskIds)
    {
        PromLightBindingSnapshot snapshot = Normalize(
            name,
            taskIds,
            DateTimeOffset.UtcNow);
        var stored = new StoredSettings(
            1,
            snapshot.Name,
            snapshot.TaskIds.ToArray(),
            snapshot.UpdatedAt.ToUnixTimeSeconds());
        ApplicationData.Current.LocalSettings.Values[SettingsKey] =
            JsonSerializer.Serialize(stored);
        return snapshot;
    }

    private static PromLightBindingSnapshot Normalize(
        string? name,
        IEnumerable<string>? taskIds,
        DateTimeOffset updatedAt)
    {
        string safeName = string.Join(
            ' ',
            (name ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (safeName.Length == 0)
        {
            safeName = "PromLight";
        }
        if (safeName.Length > 40)
        {
            safeName = safeName[..40];
        }

        string[] safeTaskIds = (taskIds ?? Array.Empty<string>())
            .Where(IsSafeTaskId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTaskCount)
            .ToArray();
        return new PromLightBindingSnapshot(
            safeName,
            true,
            safeTaskIds,
            updatedAt);
    }

    private static bool IsSafeTaskId(string? taskId) =>
        !string.IsNullOrWhiteSpace(taskId)
        && taskId.Length <= 128
        && taskId.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');
}
