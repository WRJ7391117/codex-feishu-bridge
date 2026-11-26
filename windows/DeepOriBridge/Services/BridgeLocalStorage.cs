using Windows.Storage;

namespace DeepOriBridge.Services;

internal static class BridgeLocalStorage
{
    internal static string Root => PortablePreview.Enabled
        ? PortablePreview.DataRoot : ApplicationData.Current.LocalFolder.Path;
    internal static string? Read(string key) => PortablePreview.Enabled
        ? PortablePreview.ReadSetting(key) : ApplicationData.Current.LocalSettings.Values[key] as string;
    internal static void Write(string key, string value)
    {
        if (PortablePreview.Enabled) PortablePreview.WriteSetting(key, value);
        else ApplicationData.Current.LocalSettings.Values[key] = value;
    }
}
