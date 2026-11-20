using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepOriBridge.Services;

// An explicit user-trusted channel, never a claim of publisher authentication.
internal static class PortablePreview
{
    internal const string Channel = "unsigned-portable-preview-v1";
    internal const string ManifestName = "portable-manifest.json";
    internal const string CredentialPrefix = "DeepOriBridge.Windows.Portable.Unsigned.Isolated.v1";
    internal static bool Enabled => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "DeepOriBridge.PortablePreview" && a.Value == "true");
    internal static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepOriBridge", "Portable-Unsigned-Isolated-v1");
    internal sealed record Payload(int Version, string Channel, string SourceCommit,
        Dictionary<string, string> Files);
    internal sealed record Consent(int Version, string Root, string PayloadHash);
    private static readonly List<FileStream> PayloadLocks = [];
    private static string? _acceptedHash;
    private static string? _acceptedRoot;

    internal static string[] VerifyPayload(string root)
    {
        root = Path.GetFullPath(root);
        if (FileAttestation.HasReparsePoint(root)) throw new IOException("便携目录包含重解析点。");
        string manifest = Path.Combine(root, ManifestName);
        if (new FileInfo(manifest).Length > 4 * 1024 * 1024)
            throw new IOException("便携清单超出大小限制。");
        if (FileAttestation.HasReparsePoint(manifest)) throw new IOException("清单路径不安全。");
        Payload p = JsonSerializer.Deserialize<Payload>(File.ReadAllText(manifest))
            ?? throw new IOException("便携清单无效。");
        if (p.Version != 1 || p.Channel != Channel || p.SourceCommit?.Length != 40
            || !p.SourceCommit.All(Uri.IsHexDigit) || p.Files is null || p.Files.Count is < 1 or > 4096)
            throw new IOException("便携清单版本或来源无效。");
        var actual = EnumerateFiles(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != ManifestName).Order(StringComparer.Ordinal).ToArray();
        var expected = p.Files.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)
            || expected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length
            || !expected.Contains("DeepOriBridge.exe") || !expected.Contains("DeepOriBridge.dll")
            || !expected.Contains("Tools/feishu-event-sidecar.exe"))
            throw new IOException("便携文件缺失或存在清单外文件；请重新解压完整下载包。");
        foreach (string relative in expected)
        {
            if (relative.Contains(':') || relative.Contains('\\') || relative.StartsWith('/')
                || relative.Split('/').Any(s => s is "" or "." or ".."))
                throw new IOException("便携清单包含非法路径。");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!FileAttestation.CanonicalPath(path).Equals(path, StringComparison.OrdinalIgnoreCase)
                || p.Files[relative]?.Length != 64
                || !FileAttestation.FixedTimeEquals(FileAttestation.Sha256(path), p.Files[relative]))
                throw new IOException("便携文件校验失败；未启动桥接。");
        }
        return expected.Append(ManifestName).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("便携文件包含重解析点。");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string file in EnumerateFiles(path)) yield return file;
            }
            else yield return path;
        }
    }

    internal static string LockAndVerify()
    {
        if (!Enabled) throw new InvalidOperationException("不是便携构建。");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string[] files = VerifyPayload(root);
        try
        {
            foreach (string relative in files)
                PayloadLocks.Add(new FileStream(Path.Combine(root, relative), FileMode.Open,
                    FileAccess.Read, FileShare.Read));
            files = VerifyPayload(root); // Recheck after locking all known files.
            return FileAttestation.PayloadManifestSha256(root, files);
        }
        catch
        {
            foreach (var stream in PayloadLocks) stream.Dispose();
            PayloadLocks.Clear();
            throw;
        }
    }

    internal static bool HasConsent(string hash)
    {
        try
        {
            string? text = ReadSetting("payload-consent");
            Consent? consent = text is null ? null : JsonSerializer.Deserialize<Consent>(text);
            return MatchesConsent(consent, AppContext.BaseDirectory, hash);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        { return false; }
    }
    internal static bool MatchesConsent(Consent? consent, string root, string hash) =>
        consent?.Version == 1 && hash.Length == 64
        && consent.Root == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        && consent.PayloadHash == hash;

    internal static void Accept(string hash, bool persist)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string actual = FileAttestation.PayloadManifestSha256(root, VerifyPayload(root));
        if (!FileAttestation.FixedTimeEquals(hash, actual)) throw new IOException("确认期间文件已变化。");
        if (persist) WriteSetting("payload-consent", JsonSerializer.Serialize(new Consent(1, root, hash)));
        _acceptedRoot = root;
        _acceptedHash = hash;
    }

    internal static bool IsTrusted(string root, string hash) => Enabled
        && _acceptedRoot == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        && _acceptedHash == hash;

    internal static string? ReadSetting(string key) => ReadSettingAt(DataRoot, key);
    internal static void WriteSetting(string key, string value) => WriteSettingAt(DataRoot, key, value);
    internal static string? ReadSettingAt(string root, string key)
    {
        string path = SettingPath(root, key);
        if (!File.Exists(path)) return null;
        if (FileAttestation.HasReparsePoint(path) || new FileInfo(path).Length > 1024 * 1024)
            throw new IOException("便携设置路径或大小无效。");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),
            Encoding.UTF8.GetBytes(Channel + key), DataProtectionScope.CurrentUser));
    }
    internal static void WriteSettingAt(string root, string key, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 512 * 1024) throw new IOException("便携设置超出大小限制。");
        string path = SettingPath(root, key);
        if (File.Exists(path) && FileAttestation.HasReparsePoint(path)) throw new IOException("设置路径不安全。");
        string lease = path + ".lock";
        if (File.Exists(lease) && FileAttestation.HasReparsePoint(lease)) throw new IOException("设置锁路径不安全。");
        using var gate = new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),
                    Encoding.UTF8.GetBytes(Channel + key), DataProtectionScope.CurrentUser));
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string SettingPath(string root, string key)
    {
        string directory = Path.Combine(Path.GetFullPath(root), "Settings");
        Directory.CreateDirectory(directory);
        if (FileAttestation.HasReparsePoint(directory)) throw new IOException("便携数据目录不安全。");
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    }
}
