using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeepOriBridge.Services;

internal static class AuthorizedProjectScope
{
    internal static bool Contains(string authorizedProjectPath, string? candidateWorkingDirectory) =>
        Contains(authorizedProjectPath, candidateWorkingDirectory, GitOriginResolver.Resolve);

    internal static bool Contains(
        string authorizedProjectPath,
        string? candidateWorkingDirectory,
        Func<string, string?> originResolver)
    {
        if (string.IsNullOrWhiteSpace(authorizedProjectPath)
            || string.IsNullOrWhiteSpace(candidateWorkingDirectory))
        {
            return false;
        }

        try
        {
            string root = Path.GetFullPath(authorizedProjectPath);
            string candidate = Path.GetFullPath(candidateWorkingDirectory);
            string relative = Path.GetRelativePath(root, candidate);
            if (string.Equals(relative, ".", StringComparison.Ordinal)
                || (!Path.IsPathRooted(relative)
                    && !string.Equals(relative, "..", StringComparison.Ordinal)
                    && !relative.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)))
            {
                return true;
            }

            string? authorizedOrigin = NormalizeOrigin(originResolver(root));
            string? candidateOrigin = NormalizeOrigin(originResolver(candidate));
            return authorizedOrigin is not null
                && candidateOrigin is not null
                && string.Equals(
                    authorizedOrigin,
                    candidateOrigin,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string? NormalizeOrigin(string? value)
    {
        string normalized = (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        return normalized.Length == 0 ? null : normalized;
    }

    private static class GitOriginResolver
    {
        private sealed record CacheEntry(string? Origin);

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        internal static string? Resolve(string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            return Cache.GetOrAdd(fullPath, ReadOrigin).Origin;
        }

        private static CacheEntry ReadOrigin(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return new CacheEntry(null);
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("-C");
                startInfo.ArgumentList.Add(directory);
                startInfo.ArgumentList.Add("config");
                startInfo.ArgumentList.Add("--get");
                startInfo.ArgumentList.Add("remote.origin.url");

                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("无法启动 Git 项目标识探测。");
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    return new CacheEntry(null);
                }
                string origin = process.StandardOutput.ReadToEnd().Trim();
                return process.ExitCode == 0 && origin.Length > 0
                    ? new CacheEntry(origin)
                    : new CacheEntry(null);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
            {
                return new CacheEntry(null);
            }
        }
    }
}
