using System.Text.Json;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal enum PromLightLegacyState { Missing, Valid, Invalid }

internal sealed record PromLightLegacyConfiguration(PromLightLegacyState State, PromLightBindingSnapshot? Binding)
{
    private sealed record Stored(int Version, string Name, string[] TaskIds, long UpdatedAtUnixSeconds);

    internal static PromLightLegacyConfiguration Parse(object? value)
    {
        if (value is null) return new(PromLightLegacyState.Missing, null);
        if (value is not string json || string.IsNullOrWhiteSpace(json)) return new(PromLightLegacyState.Invalid, null);
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null || stored.Version != 1 || string.IsNullOrWhiteSpace(stored.Name)
                || stored.Name.Length > 40 || stored.TaskIds is null || stored.TaskIds.Length > 50
                || stored.TaskIds.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128
                    || t.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
                return new(PromLightLegacyState.Invalid, null);
            return new(PromLightLegacyState.Valid, new(stored.Name, true, stored.TaskIds.Distinct().ToArray(),
                stored.UpdatedAtUnixSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(stored.UpdatedAtUnixSeconds) : DateTimeOffset.MinValue));
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException)
        {
            return new(PromLightLegacyState.Invalid, null);
        }
    }

    internal PromLightBindingSnapshot RequireValid() => State == PromLightLegacyState.Valid && Binding is not null
        ? Binding : throw new InvalidOperationException(State == PromLightLegacyState.Missing
            ? "没有可导入的旧提示灯配置；未创建灯记录。"
            : "旧提示灯配置损坏或版本不受支持；未导入，原配置已保留。");

    internal PromLightLamp Import(PromLightRegistry registry, string owner, string generation, string device,
        Func<string, bool> taskAllowed)
    {
        var binding = RequireValid();
        if (binding.TaskIds.Any(id => !taskAllowed(id))) throw new InvalidOperationException("旧关联含未获授权的 Task；未导入。");
        return registry.Bind(owner, generation, device, binding.Name, binding.TaskIds.ToArray());
    }
}
