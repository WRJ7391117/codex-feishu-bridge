using System.IO.Pipes;
using System.Text;
using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal sealed class CodexEnvironmentProbe
{
    private const string PrivatePipeName = "codex-ipc";

    public async Task<CodexEnvironmentSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string stateDatabase = Path.Combine(userProfile, ".codex", "state_5.sqlite");
        string catalogDatabase = Path.Combine(userProfile, ".codex", "sqlite", "codex-dev.db");
        string codexExecutable = CodexExecutableLocator.Find();
        bool pipeDetected = await DetectPrivatePipeAsync(cancellationToken);

        string version = string.Empty;
        bool initialized = false;
        string platform = string.Empty;
        bool threadListSucceeded = false;
        int returnedThreadCount = 0;
        string? error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(codexExecutable))
            {
                throw new FileNotFoundException("未找到 Codex Desktop 随附的 codex.exe。");
            }

            version = await CodexExecutableLocator.ReadVersionAsync(codexExecutable, cancellationToken);
            await using var client = new CodexAppServerClient();
            await client.ConnectAsync(codexExecutable, cancellationToken);
            CodexThreadListResult threads = await client.ListThreadsAsync(
                limit: 1,
                useStateDatabaseOnly: true,
                cancellationToken);
            initialized = true;
            platform = client.SessionInfo?.Platform ?? string.Empty;
            threadListSucceeded = true;
            returnedThreadCount = threads.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
        }

        return new CodexEnvironmentSnapshot(
            Environment.OSVersion.VersionString,
            codexExecutable,
            version,
            stateDatabase,
            HasSqliteHeader(stateDatabase),
            catalogDatabase,
            HasSqliteHeader(catalogDatabase),
            pipeDetected,
            initialized,
            platform,
            threadListSucceeded,
            returnedThreadCount,
            error,
            DateTimeOffset.Now);
    }

    private static bool HasSqliteHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Read(header) == header.Length
                && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> DetectPrivatePipeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PrivatePipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await pipe.ConnectAsync(timeout.Token);
            return pipe.IsConnected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EnumeratePipeNames().Contains(PrivatePipeName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EnumeratePipeNames().Contains(PrivatePipeName, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> EnumeratePipeNames()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
