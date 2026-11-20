namespace DeepOriBridge.Services;

// Device enumeration order is not identity. Never move an existing binding to
// another physical lamp because the original lamp disconnected.
internal static class PromLightDeviceSelectionPolicy
{
    internal static string? Select(IEnumerable<string> enabledDeviceIds, string? pinnedDeviceId)
    {
        string[] ids = enabledDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!string.IsNullOrEmpty(pinnedDeviceId))
            return ids.SingleOrDefault(id => id.Equals(pinnedDeviceId, StringComparison.OrdinalIgnoreCase));
        return ids.Length == 1 ? ids[0] : null;
    }
}
