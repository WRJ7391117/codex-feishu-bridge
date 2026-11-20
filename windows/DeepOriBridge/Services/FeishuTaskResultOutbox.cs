using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepOriBridge.Services;

internal sealed record FeishuResultPart(string Text, string Key,
    string? ResourceFile = null, string? ResourceKind = null, string? FileName = null);
internal sealed record FeishuResultEnvelope(string Generation, string Sender, string WorkingDirectory,
    FeishuResultPart[] Parts, int Next = 0);

// Result-only outbox. It never stores a runnable prompt or starts a Codex turn.
// Persist outside the checkout and protect contents with this Windows user's DPAPI.
internal sealed class FeishuTaskResultOutbox
{
    private readonly string _directory;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal FeishuTaskResultOutbox(string? directory = null,
        Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepOriBridge", "Development-Unsigned-Isolated", "TaskResultOutbox");
        _protect = protect ?? (data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
    }

    internal async Task SaveAsync(string id, FeishuResultEnvelope envelope, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_directory);
            string path = FilePath(id);
            if (!File.Exists(path)) Write(path, envelope);
        }
        finally { _gate.Release(); }
    }

    internal async Task SendPartAsync(string id, string key,
        Func<FeishuResultEnvelope, bool> allowed,
        Func<FeishuResultEnvelope, FeishuResultPart, CancellationToken, Task> send,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            string path = FilePath(id);
            FeishuResultEnvelope envelope = Read(path);
            if (!allowed(envelope)) throw new InvalidOperationException("原结果的身份或项目授权已变化，停止投递。");
            int index = Array.FindIndex(envelope.Parts, part => part.Key == key);
            if (index < 0) throw new InvalidOperationException("结果分段引用无效。");
            if (index < envelope.Next) return;
            if (index != envelope.Next) throw new InvalidOperationException("结果分段必须按顺序发送。");
            await send(envelope, envelope.Parts[index], token);
            // Keep a completed receipt, so a delayed local retry cannot recreate a sent result.
            Write(path, envelope with { Next = index + 1 });
        }
        finally { _gate.Release(); }
    }

    internal async Task FlushAsync(Func<FeishuResultEnvelope, bool> allowed,
        Func<FeishuResultEnvelope, FeishuResultPart, CancellationToken, Task> send,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (string path in Directory.EnumerateFiles(_directory, "*.bin"))
            {
                token.ThrowIfCancellationRequested();
                FeishuResultEnvelope envelope;
                try { envelope = Read(path); }
                catch (Exception exception) when (exception is IOException or CryptographicException or JsonException) { continue; }
                if (envelope.Next >= envelope.Parts.Length || !allowed(envelope)) continue;
                while (envelope.Next < envelope.Parts.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (!allowed(envelope)) break;
                    await send(envelope, envelope.Parts[envelope.Next], token);
                    envelope = envelope with { Next = envelope.Next + 1 };
                    Write(path, envelope);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private string FilePath(string id) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin");

    private FeishuResultEnvelope Read(string path) =>
        JsonSerializer.Deserialize<FeishuResultEnvelope>(_unprotect(File.ReadAllBytes(path)))
        ?? throw new JsonException("Invalid result envelope");

    private void Write(string path, FeishuResultEnvelope envelope)
    {
        // Once fully delivered retain only the deduplication receipt, not the result text.
        if (envelope.Next == envelope.Parts.Length)
            envelope = envelope with { Parts = envelope.Parts.Select(p => p with { Text = "" }).ToArray() };
        byte[] data = _protect(JsonSerializer.SerializeToUtf8Bytes(envelope));
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
