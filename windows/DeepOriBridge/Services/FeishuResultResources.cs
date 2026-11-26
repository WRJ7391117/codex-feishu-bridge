using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepOriBridge.Services;

// Immutable encrypted snapshots outlive a Task and can be retried after restart.
// Only explicit Markdown references to regular files inside the authorized Task
// directory are eligible. Remote URLs and paths outside that root are not fetched.
internal sealed class FeishuResultResources(string directory)
{
    private static readonly Regex Links = new(@"!?\[[^\]\r\n]*\]\(\s*(?<path><[^>\r\n]+>|[^)\r\n]+)\s*\)", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".c", ".cpp", ".cs", ".csv", ".doc", ".docx", ".go", ".h", ".hpp", ".html", ".java", ".js", ".json", ".jsx", ".kt", ".kts", ".md", ".pdf", ".ppt", ".pptx", ".py", ".rs", ".rtf", ".sh", ".sql", ".swift", ".ts", ".tsx", ".txt", ".xls", ".xlsx", ".xml", ".yaml", ".yml", ".zsh" };

    internal async Task<FeishuResultPart[]> CaptureAsync(string id, string text, string workingDirectory,
        Func<int, string> key, CancellationToken token)
    {
        var parts = new List<FeishuResultPart>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>();
        string root = Path.GetFullPath(workingDirectory);
        foreach (Match match in Links.Matches(text))
        {
            token.ThrowIfCancellationRequested();
            string raw = match.Groups["path"].Value.Trim().Trim('<', '>');
            if (raw.Contains("://", StringComparison.Ordinal)) continue;
            string path;
            try
            {
                // Relative links refer to the Task cwd, never the Bridge's cwd.
                path = Path.GetFullPath(Uri.UnescapeDataString(raw), root);
                if (!AuthorizedProjectScope.Contains(root, path, _ => null) || !seen.Add(path) || !File.Exists(path)) continue;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { continue; }
            string extension = Path.GetExtension(path);
            string? kind = Images.Contains(extension) ? "image" : Audio.Contains(extension) ? "audio" : Documents.Contains(extension) ? "file" : null;
            if (kind is null) continue;
            int count = counts.GetValueOrDefault(kind);
            if (count >= (kind == "image" ? 8 : 4)) continue;
            try
            {
                FeishuAttachments.ValidateLocalFile(path, kind == "image" ? 20L * 1024 * 1024 : 50L * 1024 * 1024);
                Directory.CreateDirectory(directory);
                string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\0" + path))) + ".bin";
                string target = Path.Combine(directory, name);
                if (!File.Exists(target))
                {
                    // Deny writes/deletes while snapshotting; cap bytes even if the
                    // source was replaced between metadata validation and open.
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string openedPath = FileAttestation.CanonicalPath(source.SafeFileHandle);
                    if (!openedPath.Equals(path, StringComparison.OrdinalIgnoreCase)
                        || !AuthorizedProjectScope.Contains(root, openedPath, _ => null))
                        throw new InvalidOperationException("结果附件路径在读取前发生变化。");
                    if (source.Length is <= 0 || source.Length > (kind == "image" ? 20L : 50L) * 1024 * 1024)
                        throw new InvalidOperationException("结果附件为空或超过大小限制。");
                    byte[] data = new byte[checked((int)source.Length)];
                    await source.ReadExactlyAsync(data, token);
                    byte[] protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                    string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await File.WriteAllBytesAsync(temp, protectedData, token);
                        try { File.Move(temp, target, overwrite: false); }
                        catch (IOException) when (File.Exists(target)) { }
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                counts[kind] = count + 1;
                parts.Add(new("结果附件：" + Path.GetFileName(path), key(parts.Count), target, kind, Path.GetFileName(path)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                parts.Add(new("结果附件未上传：" + Path.GetFileName(path) + "（文件不可读、路径不安全或大小超限）。", key(parts.Count)));
            }
        }
        return parts.ToArray();
    }

    internal async Task<string> MaterializeAsync(FeishuResultPart part, string targetDirectory, CancellationToken token)
    {
        string path = part.ResourceFile ?? throw new InvalidOperationException("缺少结果附件快照。");
        if (!AuthorizedProjectScope.Contains(directory, path, _ => null)) throw new InvalidOperationException("结果附件快照路径越界。");
        FeishuAttachments.ValidateLocalFile(path, 51L * 1024 * 1024);
        byte[] data = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, token), null, DataProtectionScope.CurrentUser);
        string destination = Path.Combine(targetDirectory, Guid.NewGuid().ToString("N") + ".download");
        await File.WriteAllBytesAsync(destination, data, token);
        var reference = new FeishuInputResource(part.ResourceKind ?? "file", "", part.FileName ?? "result.txt");
        return FeishuAttachments.ValidateDownloaded(destination, reference).Path;
    }
}
