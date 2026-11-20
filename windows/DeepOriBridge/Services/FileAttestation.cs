using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeepOriBridge.Services;

internal static class FileAttestation
{
    private const uint FileNameNormalized = 0;

    internal static string CanonicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return CanonicalPath(handle);
    }

    internal static string CanonicalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, FileNameNormalized);
        if (length == 0 || length >= builder.Capacity)
        {
            throw new IOException("无法取得文件的规范 Windows 路径。");
        }

        string resolved = builder.ToString();
        return resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + resolved[8..]
            : resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? resolved[4..]
                : resolved;
    }

    internal static bool HasReparsePoint(string path, string? trustedRoot = null)
    {
        string fullPath = Path.GetFullPath(path);
        string? stop = string.IsNullOrWhiteSpace(trustedRoot)
            ? Path.GetPathRoot(fullPath)
            : Path.GetFullPath(trustedRoot);
        FileSystemInfo? current = File.Exists(fullPath)
            ? new FileInfo(fullPath)
            : Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : null;
        if (current is null)
        {
            return true;
        }

        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (stop is not null
                && current.FullName.Equals(stop, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        return false;
    }

    internal static string Sha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string ProcessImagePath(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var builder = new StringBuilder(32768);
        uint length = (uint)builder.Capacity;
        if (!QueryFullProcessImageName(
                process.SafeHandle,
                flags: 0,
                builder,
                ref length)
            || length == 0)
        {
            throw new IOException(
                "无法取得进程的 Windows 镜像路径。",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return Path.GetFullPath(builder.ToString());
    }

    internal static string PayloadManifestSha256(
        string trustedRoot,
        IEnumerable<string> relativePaths)
    {
        string canonicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(trustedRoot));
        var manifest = new StringBuilder();
        foreach (string relativePath in relativePaths
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathFullyQualified(relativePath)
                || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is ".." or "."))
            {
                throw new InvalidOperationException("payload manifest 包含非法相对路径。");
            }

            string expected = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
            if (!expected.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(expected)
                || HasReparsePoint(expected, canonicalRoot))
            {
                throw new InvalidOperationException("payload manifest 文件缺失、越界或包含重解析点。");
            }

            string canonical = CanonicalPath(expected);
            if (!canonical.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("payload manifest 规范路径不匹配。");
            }

            var info = new FileInfo(canonical);
            manifest.Append(relativePath.Replace('\\', '/'))
                .Append('\0')
                .Append(info.Length)
                .Append('\0')
                .Append(Sha256(canonical))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    internal static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);
}
