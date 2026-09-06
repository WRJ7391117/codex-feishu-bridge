using System.Text.Json;

namespace DeepOriBridge.Services;

internal sealed record FeishuInputResource(string Kind, string Key, string Name);
public sealed record CodexInputAttachment(string Kind, string Path, string Name);

internal static class FeishuAttachments
{
    private static readonly object DiagnosticGate = new();

    // Bounded operational evidence only. No message text, resource keys, file
    // names, local input paths or credentials are retained in diagnostics.
    internal static void Trace(string messageId, string stage, int? count = null, JsonElement? resourceEvent = null)
    {
        if (stage is not ("received" or "rejected" or "downloaded" or "submitted" or "completed" or "pending"
            or "queued" or "submitting" or "duplicate-suppressed")) return;
        try
        {
            lock (DiagnosticGate)
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepOriBridge", "Development-Unsigned-Isolated", "Diagnostics");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "attachments.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                string correlation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(messageId)))[..16];
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow, correlation, stage,
                    count = count is >= 0 and <= 50 ? count : null,
                    diagnostic = resourceEvent is JsonElement root ? ReadDiagnostic(root) : "",
                }) + "\n");
            }
        }
        catch (Exception) { /* Diagnostics cannot suppress input or feedback. */ }
    }

    internal static string ReadDiagnostic(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resource_error_code", out JsonElement codeElement)
            || codeElement.ValueKind != JsonValueKind.String) return "";
        string? code = codeElement.GetString();
        if (code is not ("missing_content" or "content_too_large" or "invalid_json" or "invalid_content"
            or "invalid_field" or "unsupported_field" or "unsupported_tag" or "resource_conflict"
            or "resource_limit" or "interactive_only" or "empty_content" or "unsupported_message_type")) return "";
        if (!root.TryGetProperty("resource_error_path", out JsonElement pathElement)
            || pathElement.ValueKind != JsonValueKind.String) return code;
        string path = pathElement.GetString()!;
        // Paths contain schema names and numeric indices only, never the name of
        // an unknown sender-supplied property (which itself might contain a secret).
        const string pattern = @"\A(?:message|post)(?:\.(?:message_type|locale|title|content_v2|content|tag|text|style|un_escape|lines|image_key|file_key|file_name|width|height|duration|image_type|href|user_id|user_name|unknown)|\[[0-9]{1,5}\]){0,8}\z";
        return path.Length <= 160 && System.Text.RegularExpressions.Regex.IsMatch(path, pattern,
            System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))
                ? code + " @ " + path : code;
    }

    internal const int MaxInputImages = 4;
    internal const int MaxInputFiles = 4;
    internal const long MaxImageBytes = 20L * 1024 * 1024;
    internal const long MaxFileBytes = 50L * 1024 * 1024;

    // Keep in sync with DOCUMENT_SUFFIXES and AUDIO_SUFFIXES in the Mac bridge.
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cpp", ".cs", ".csv", ".doc", ".docx", ".go", ".h", ".hpp",
        ".html", ".java", ".js", ".json", ".jsx", ".kt", ".kts", ".md",
        ".pdf", ".ppt", ".pptx", ".py", ".rs", ".rtf", ".sh", ".sql",
        ".swift", ".ts", ".tsx", ".txt", ".xls", ".xlsx", ".xml", ".yaml",
        ".yml", ".zsh",
    };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav" };

    internal static IReadOnlyList<FeishuInputResource> ReadResources(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("附件消息格式无效。");
        if (!root.TryGetProperty("resources", out JsonElement resources)) return [];
        if (resources.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("附件列表格式无效。");

        var result = new List<FeishuInputResource>();
        int images = 0, files = 0;
        foreach (JsonElement item in resources.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("附件条目格式无效。");
            string kind = RequiredString(item, "kind");
            string key = RequiredString(item, "key");
            if (kind is not ("image" or "file" or "audio"))
                throw new InvalidOperationException("当前仅支持图片、文档和音频附件。");
            string prefix = kind == "image" ? "img_" : "file_";
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || key.Length < prefix.Length + 3 || key.Length > prefix.Length + 512
                || key.AsSpan(prefix.Length).ContainsAnyExcept(
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"))
                throw new InvalidOperationException("附件资源标识无效，请重新发送。");

            string name = "";
            if (item.TryGetProperty("name", out JsonElement nameElement))
            {
                if (nameElement.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("附件名称格式无效。");
                name = nameElement.GetString()!;
            }
            ValidateName(name, kind);
            if (kind == "image") images++; else files++;
            if (images > MaxInputImages || files > MaxInputFiles)
                throw new InvalidOperationException("一次最多发送 4 张图片和 4 个文件（含音频），请分开发送。");
            result.Add(new(kind, key, name));
        }
        return result.ToArray();
    }

    // The caller must own the download directory and enforce its scope. Only a
    // staging .download file is renamed; sender names never supply output paths.
    internal static CodexInputAttachment ValidateDownloaded(string path, FeishuInputResource resource)
    {
        if (resource is null || resource.Kind is not ("image" or "file" or "audio"))
            throw new InvalidOperationException("附件类型无效。");
        ValidateName(resource.Name, resource.Kind);
        long limit = resource.Kind == "image" ? MaxImageBytes : MaxFileBytes;
        ValidateLocalFile(path, limit);
        try
        {
            string fullPath = Path.GetFullPath(path);
            string originalExtension = Path.GetExtension(fullPath);
            bool staged = originalExtension.Equals(".download", StringComparison.OrdinalIgnoreCase);
            string label = LeafName(resource.Name);
            string extension;
            string kind = resource.Kind;
            if (kind == "image")
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length <= 0 || stream.Length > limit)
                    throw new InvalidOperationException("附件为空或超过大小限制。");
                Span<byte> header = stackalloc byte[12];
                int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                extension = ImageExtension(header[..read]);
                if (extension.Length == 0)
                    throw new InvalidOperationException("当前仅支持 PNG、JPEG、GIF 和 WebP 图片。");
            }
            else
            {
                extension = (staged ? Path.GetExtension(label) : originalExtension).ToLowerInvariant();
                if (extension.Length == 0 && kind == "audio") extension = ".opus";
                if (!Documents.Contains(extension) && !Audio.Contains(extension))
                    throw new InvalidOperationException("当前不支持该文件类型；暂不自动处理压缩包或可执行文件。");
                if (kind == "audio" && !Audio.Contains(extension))
                    throw new InvalidOperationException("音频附件的文件类型无效。");
                if (Audio.Contains(extension)) kind = "audio";
            }

            if (staged)
            {
                // A fresh basename also avoids collisions and reserved Windows names.
                string target = Path.Combine(Path.GetDirectoryName(fullPath)!, Guid.NewGuid().ToString("N") + extension);
                ValidateLocalFile(fullPath, limit);
                File.Move(fullPath, target, overwrite: false);
                fullPath = target;
            }
            else if (!originalExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("附件扩展名与内容不符，请重新下载附件。");
            }
            ValidateLocalFile(fullPath, limit);
            if (string.IsNullOrWhiteSpace(label)) label = Path.GetFileName(fullPath);
            else if (kind == "image" || Path.GetExtension(label).Length == 0)
                label = Path.ChangeExtension(label, extension);
            return new(kind, fullPath, label);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw new InvalidOperationException("无法安全读取或准备附件，请重新发送。");
        }
    }

    internal static object[] BuildTurnInput(string prompt, IReadOnlyList<CodexInputAttachment>? attachments)
    {
        var input = new List<object> { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } };
        foreach (CodexInputAttachment attachment in attachments ?? [])
        {
            input.Add(attachment.Kind switch
            {
                "image" => (object)new { type = "localImage", path = attachment.Path },
                "audio" => new { type = "localAudio", path = attachment.Path },
                "file" => new { type = "mention", name = attachment.Name, path = attachment.Path },
                _ => throw new InvalidOperationException("附件类型无效。"),
            });
        }
        return input.ToArray();
    }

    internal static object[] BuildContextAttachments(IReadOnlyList<CodexInputAttachment>? attachments)
        => (attachments ?? []).Where(attachment => attachment.Kind != "image").Select(attachment => (object)new
        {
            label = attachment.Name, path = attachment.Path, fsPath = attachment.Path,
        }).ToArray();

    // Scope authorization belongs to the caller. This checks path syntax, every
    // ancestor, the file itself, and its size; it does not grant access to a root.
    // Callers reading later must still cap their read and prevent concurrent writes.
    internal static void ValidateLocalFile(string path, long maxBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0 || !Path.IsPathFullyQualified(path)
                || path.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal)
                || path.Any(char.IsControl))
                throw new InvalidOperationException("附件路径不安全或不是本地绝对路径。");
            int start = OperatingSystem.IsWindows() ? 2 : 0;
            if (path.AsSpan(start).Contains(':'))
                throw new InvalidOperationException("附件路径不允许使用备用数据流。");
            string root = Path.GetPathRoot(path)!;
            foreach (string segment in path[root.Length..].Split(['/', '\\']))
            {
                if (segment.Length == 0 || segment is "." or ".."
                    || segment.EndsWith(' ') || segment.EndsWith('.')
                    || segment.IndexOfAny(['<', '>', '"', '|', '?', '*', ':']) >= 0
                    || IsDeviceName(segment))
                    throw new InvalidOperationException("附件路径包含不安全的文件名。");
            }
            string fullPath = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidOperationException("附件必须是普通文件，不能使用目录或链接。");
            for (DirectoryInfo? parent = Directory.GetParent(fullPath); parent is not null; parent = parent.Parent)
            {
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("附件路径不能经过链接或重解析目录。");
            }
            long length = new FileInfo(fullPath).Length;
            if (length <= 0) throw new InvalidOperationException("收到的附件为空。");
            if (length > maxBytes) throw new InvalidOperationException("附件超过允许的大小限制。");
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // Never echo a sender-controlled path, key, or raw system exception.
            throw new InvalidOperationException("附件不存在、无法读取或路径不安全。");
        }
    }

    private static string RequiredString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("附件缺少有效的类型或资源标识。");
        return value.GetString()!;
    }

    private static void ValidateName(string name, string kind)
    {
        if (name is null || name.Length > 1024 || name.Any(char.IsControl))
            throw new InvalidOperationException("附件名称无效。");
        if (kind != "image" && name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidOperationException("附件名称不能包含目录、盘符或备用数据流。");
    }

    private static string LeafName(string name)
        => name.Replace('\\', '/').Split('/')[^1];

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }

    private static bool IsFileFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    private static string ImageExtension(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return ".png";
        if (header.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) return ".gif";
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return "";
    }
}
