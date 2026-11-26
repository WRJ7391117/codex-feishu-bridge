using System.Security.Cryptography;
using System.Text.Json;

namespace DeepOriBridge.Services;

// One downloaded message owns an isolated directory; no sender-controlled paths.
internal sealed class FeishuAttachmentSession(string threadId = "", string queueId = "") : IDisposable
{
    private const string MarkerName = ".deepori-input-owner.bin";
    private sealed record Marker(string DirectoryName, string ThreadId, string QueueId, string TurnId,
        DateTimeOffset CreatedAt, int ProcessId, long ProcessStartedAt);
    private string? _directory;
    private string _turnId = "";
    private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    internal List<CodexInputAttachment> Attachments { get; } = [];
    internal bool MayBeInUse { get; set; }

    internal string NewDownloadPath()
    {
        if (_directory is null)
        {
            _directory = Directory.CreateTempSubdirectory("deepori-feishu-input-").FullName;
            SaveMarker();
        }
        return Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".download");
    }

    internal void RecordTurn(string turnId)
    {
        _turnId = turnId;
        SaveMarker();
    }

    private void SaveMarker()
    {
        if (_directory is null) return;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker(Path.GetFileName(_directory), threadId, queueId,
            _turnId, _createdAt, process.Id, process.StartTime.ToUniversalTime().Ticks));
        string path = Path.Combine(_directory, MarkerName);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    internal static void Sweep(CodexRolloutTaskStateReader reader, Func<string, bool> queueOwns, IEnumerable<string>? fixtureDirectories = null)
    {
        foreach (string directory in (fixtureDirectories ?? Directory.EnumerateDirectories(Path.GetTempPath(), "deepori-feishu-input-*")).Take(2000))
        {
            try
            {
                if (!SafeDirectory(directory)) continue;
                string file = Path.Combine(directory, MarkerName);
                if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(file).Length > 16384) continue;
                Marker? marker = JsonSerializer.Deserialize<Marker>(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
                if (marker is null || marker.DirectoryName != Path.GetFileName(directory)) continue;
                bool terminal = marker.ThreadId.Length > 0 && marker.TurnId.Length > 0
                    && reader.ReadTerminalResult(marker.ThreadId, marker.TurnId) is not null;
                bool orphan = marker.CreatedAt < DateTimeOffset.UtcNow.AddDays(-7)
                    && !ProcessStillOwns(marker) && !queueOwns(marker.QueueId)
                    && (marker.ThreadId.Length == 0 || reader.ReadLatestStatus(marker.ThreadId) is DeepOriBridge.Models.PromLightStatus.Idle or DeepOriBridge.Models.PromLightStatus.Error);
                if (terminal || orphan) DeleteOwnedFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
            { /* Unknown/unreadable ownership is never permission to delete. */ }
        }
    }

    private static bool ProcessStillOwns(Marker marker)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(marker.ProcessId); return process.StartTime.ToUniversalTime().Ticks == marker.ProcessStartedAt; }
        catch { return false; }
    }

    private static bool SafeDirectory(string directory) => Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
        && Path.GetFileName(directory).StartsWith("deepori-feishu-input-", StringComparison.Ordinal)
        && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;

    private static void DeleteOwnedFiles(string directory)
    {
        if (!SafeDirectory(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
        Directory.Delete(directory, recursive: false);
    }

    public void Dispose()
    {
        if (MayBeInUse || _directory is null) return;
        try
        {
            // Non-recursive cleanup: only this session's files. Never follow links.
            DeleteOwnedFiles(_directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
