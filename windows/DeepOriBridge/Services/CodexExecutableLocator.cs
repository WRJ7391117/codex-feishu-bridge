using System.Diagnostics;

namespace DeepOriBridge.Services;

internal static class CodexExecutableLocator
{
    public static string Find()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string codexBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (!Directory.Exists(codexBin))
        {
            return string.Empty;
        }

        try
        {
            return Directory.EnumerateFiles(codexBin, "codex.exe", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static async Task<string> ReadVersionAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable, "--version")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Codex 进程。");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output.Trim();
    }
}
