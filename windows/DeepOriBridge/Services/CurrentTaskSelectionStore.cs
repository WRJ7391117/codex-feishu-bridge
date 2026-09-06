using System.Text.Json;
using Windows.Storage;

namespace DeepOriBridge.Services;

internal sealed record CurrentTaskSelection(string ThreadId, string ProjectRoot, string DesktopProjectId)
{
    internal static readonly CurrentTaskSelection Empty = new("", "", "");
}

// Stores only an explicit routing reference, never a prompt, Secret or Task content.
// The key is derived from the isolated product namespace, Bot and owner generations
// and the authorized directory. A record is not an authorization grant.
internal sealed class CurrentTaskSelectionStore(
    Func<string?> scope,
    Func<string, string?> read,
    Action<string, string> write,
    Func<string, bool>? rootAllowed = null)
{
    private sealed record Stored(int Version, CurrentTaskSelection Selection);
    internal string? Scope => scope();

    internal static CurrentTaskSelectionStore ForApp(Func<string?> scope, Func<string?> authorizedRoot) => new(
        scope,
        key => BridgeLocalStorage.Read("CurrentTask.v1." + key),
        (key, value) => BridgeLocalStorage.Write("CurrentTask.v1." + key, value),
        root => authorizedRoot() is { Length: > 0 } allowed && AuthorizedProjectScope.Contains(allowed, root));

    internal CurrentTaskSelection Load(string key)
    {
        string? json = read(key);
        if (string.IsNullOrWhiteSpace(json)) return CurrentTaskSelection.Empty;
        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(json);
            CurrentTaskSelection? value = stored?.Selection;
            return stored?.Version == 1 && value is not null
                && !string.IsNullOrWhiteSpace(value.ThreadId)
                && value.ThreadId.Length <= 128
                && value.DesktopProjectId is not null
                && value.DesktopProjectId.Length <= 128
                && !string.IsNullOrWhiteSpace(value.ProjectRoot)
                && Path.IsPathFullyQualified(value.ProjectRoot)
                && (rootAllowed?.Invoke(value.ProjectRoot) ?? true)
                ? value : CurrentTaskSelection.Empty;
        }
        catch (JsonException) { return CurrentTaskSelection.Empty; }
    }

    internal void Save(string key, CurrentTaskSelection value)
    {
        if (value != CurrentTaskSelection.Empty && !(rootAllowed?.Invoke(value.ProjectRoot) ?? true))
            throw new InvalidOperationException("Task 工作目录不在当前授权范围内；未保存选择。");
        write(key, JsonSerializer.Serialize(new Stored(1, value)));
    }
}
