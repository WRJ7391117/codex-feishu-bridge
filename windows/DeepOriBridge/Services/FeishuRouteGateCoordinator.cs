using System.Security.Cryptography;
using System.Text;

namespace DeepOriBridge.Services;

internal enum FeishuRouteKind
{
    Controlled,
    ReadOnly,
}

internal sealed record FeishuRouteAuthorization(
    FeishuRouteKind Kind,
    long Generation,
    string MessageId,
    string? Prompt,
    string ProjectPath,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    CancellationToken SessionCancellation);

internal sealed record FeishuRouteGateState(
    long Generation,
    bool Listening,
    bool OwnerBound,
    bool ControlledArmed,
    bool ControlledPending,
    bool ControlledClaimed,
    bool ReadOnlyArmed,
    bool ReadOnlyPending,
    bool ReadOnlyClaimed,
    string? ReadOnlyPrompt,
    DateTimeOffset? ControlledCapturedAt,
    DateTimeOffset? ReadOnlyCapturedAt,
    DateTimeOffset? ControlledArmedExpiresAt,
    DateTimeOffset? ReadOnlyArmedExpiresAt,
    long ControlledArmVersion,
    long ReadOnlyArmVersion);

internal sealed record FeishuRouteCaptureResult(
    bool Duplicate,
    bool ControlledCaptured,
    bool ReadOnlyCaptured,
    FeishuRouteGateState State);

internal sealed class FeishuRouteGateCoordinator : IDisposable
{
    internal static readonly TimeSpan AuthorizationTtl = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ArmedTtl = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan DedupeTtl = TimeSpan.FromMinutes(10);
    internal const int DedupeCapacity = 512;
    internal const int RetiredSessionCapacity = 8;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTimeOffset> _seenMessages = new(StringComparer.Ordinal);
    private readonly Queue<(string Digest, DateTimeOffset SeenAt)> _seenOrder = new();
    private readonly Queue<CancellationTokenSource> _retiredSessions = new();
    private long _generation;
    private bool _listening;
    private bool _ownerBound;
    private bool _controlledArmed;
    private bool _readOnlyArmed;
    private long? _controlledArmedAtTimestamp;
    private long? _readOnlyArmedAtTimestamp;
    private DateTimeOffset? _controlledArmedExpiresAt;
    private DateTimeOffset? _readOnlyArmedExpiresAt;
    private long _controlledArmVersion;
    private long _readOnlyArmVersion;
    private Ticket? _controlledTicket;
    private Ticket? _readOnlyTicket;
    private CancellationTokenSource _session = new();
    private bool _disposed;

    internal FeishuRouteGateCoordinator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal FeishuRouteGateState State
    {
        get
        {
            lock (_sync)
            {
                ExpireArmedUnsafe();
                return SnapshotUnsafe();
            }
        }
    }

    internal CancellationToken SessionCancellation
    {
        get
        {
            lock (_sync)
            {
                return _session.Token;
            }
        }
    }

    internal int RetiredSessionCount
    {
        get
        {
            lock (_sync)
            {
                return _retiredSessions.Count;
            }
        }
    }

    internal void BeginSession(bool ownerBound)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            InvalidateUnsafe(listening: false, ownerBound);
        }
    }

    internal FeishuRouteGateState SetListening(bool listening)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!listening)
            {
                InvalidateUnsafe(listening: false, _ownerBound);
            }
            else
            {
                EnsureActiveSessionUnsafe();
                _listening = true;
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteGateState SetOwnerBound(bool ownerBound)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (ownerBound != _ownerBound)
            {
                InvalidateUnsafe(_listening, ownerBound);
            }
            else
            {
                _ownerBound = ownerBound;
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteGateState Arm(FeishuRouteKind kind)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_listening)
            {
                throw new InvalidOperationException("请先启动飞书消息长连接。");
            }

            if (!_ownerBound)
            {
                throw new InvalidOperationException("请先绑定 owner-only 白名单。");
            }

            if (kind == FeishuRouteKind.Controlled)
            {
                if (_controlledTicket is not null)
                {
                    throw new InvalidOperationException("已有一条路由测试等待处理，请先清除它。");
                }

                _controlledArmed = true;
                _controlledArmedAtTimestamp = _timeProvider.GetTimestamp();
                _controlledArmedExpiresAt = _timeProvider.GetUtcNow() + ArmedTtl;
                _controlledArmVersion++;
            }
            else
            {
                if (_readOnlyTicket is not null)
                {
                    throw new InvalidOperationException("已有一条只读提示词等待处理，请先清除它。");
                }

                _readOnlyArmed = true;
                _readOnlyArmedAtTimestamp = _timeProvider.GetTimestamp();
                _readOnlyArmedExpiresAt = _timeProvider.GetUtcNow() + ArmedTtl;
                _readOnlyArmVersion++;
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteGateState Clear(FeishuRouteKind kind)
    {
        lock (_sync)
        {
            if (kind == FeishuRouteKind.Controlled)
            {
                ClearGateUnsafe(FeishuRouteKind.Controlled);
            }
            else
            {
                ClearGateUnsafe(FeishuRouteKind.ReadOnly);
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteCaptureResult ProcessMessage(
        string messageId,
        string approvedAction,
        string? readOnlyPrompt,
        bool approvedTarget,
        bool ownerMatches,
        DateTimeOffset now,
        string? projectPath = null)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (ExpireArmedUnsafe())
            {
                return new(false, false, false, SnapshotUnsafe());
            }

            if (!approvedTarget || string.IsNullOrWhiteSpace(messageId))
            {
                return new(false, false, false, SnapshotUnsafe());
            }

            bool duplicate = !RememberMessageUnsafe(messageId, now);
            if (duplicate)
            {
                return new(true, false, false, SnapshotUnsafe());
            }

            if (!_listening || !_ownerBound || !ownerMatches)
            {
                return new(false, false, false, SnapshotUnsafe());
            }

            bool controlledCaptured = false;
            bool readOnlyCaptured = false;
            if (_controlledArmed
                && (string.Equals(approvedAction, "codex-route-test", StringComparison.Ordinal)
                    || string.Equals(approvedAction, "unmatched", StringComparison.Ordinal)))
            {
                _controlledArmed = false;
                _controlledArmedAtTimestamp = null;
                _controlledArmedExpiresAt = null;
                _controlledArmVersion++;
                _controlledTicket = NewTicket(
                    messageId,
                    prompt: null,
                    projectPath,
                    now,
                    _controlledArmVersion);
                controlledCaptured = true;
            }
            else if (_readOnlyArmed
                && string.Equals(approvedAction, "codex-readonly", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(readOnlyPrompt))
            {
                _readOnlyArmed = false;
                _readOnlyArmedAtTimestamp = null;
                _readOnlyArmedExpiresAt = null;
                _readOnlyArmVersion++;
                _readOnlyTicket = NewTicket(
                    messageId,
                    readOnlyPrompt.Trim(),
                    projectPath,
                    now,
                    _readOnlyArmVersion);
                readOnlyCaptured = true;
            }

            return new(false, controlledCaptured, readOnlyCaptured, SnapshotUnsafe());
        }
    }

    internal FeishuRouteAuthorization Claim(FeishuRouteKind kind, DateTimeOffset now)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_listening || !_ownerBound)
            {
                throw new InvalidOperationException("飞书会话或 owner 授权已失效；请重新捕获消息。");
            }

            Ticket? ticket = kind == FeishuRouteKind.Controlled
                ? _controlledTicket
                : _readOnlyTicket;
            if (ticket is null || ticket.Generation != _generation)
            {
                throw new InvalidOperationException("没有当前会话中等待授权的路由消息。");
            }

            if (IsTicketExpiredUnsafe(ticket))
            {
                if (kind == FeishuRouteKind.Controlled)
                {
                    _controlledTicket = null;
                }
                else
                {
                    _readOnlyTicket = null;
                }

                throw new InvalidOperationException("人工 Gate 已超过 120 秒有效期；请重新捕获消息。");
            }

            if (ticket.Claimed)
            {
                throw new InvalidOperationException("该人工 Gate 已被执行，不能重复创建 Codex Task。");
            }

            ticket.Claimed = true;
            return new(
                kind,
                ticket.Generation,
                ticket.MessageId,
                ticket.Prompt,
                ticket.ProjectPath,
                ticket.CapturedAt,
                ticket.ExpiresAt,
                _session.Token);
        }
    }

    internal FeishuRouteGateState Complete(FeishuRouteAuthorization authorization)
    {
        lock (_sync)
        {
            Ticket ticket = ValidateAuthorizationUnsafe(authorization);
            if (!ticket.Claimed)
            {
                throw new InvalidOperationException("路由授权票据尚未 claim。");
            }

            if (authorization.Kind == FeishuRouteKind.Controlled)
            {
                _controlledTicket = null;
            }
            else
            {
                _readOnlyTicket = null;
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteGateState Abort(FeishuRouteAuthorization authorization)
    {
        lock (_sync)
        {
            if (authorization.Generation != _generation)
            {
                return SnapshotUnsafe();
            }

            Ticket? ticket = authorization.Kind == FeishuRouteKind.Controlled
                ? _controlledTicket
                : _readOnlyTicket;
            if (ticket is null || ticket.MessageId != authorization.MessageId)
            {
                return SnapshotUnsafe();
            }

            if (authorization.Kind == FeishuRouteKind.Controlled)
            {
                _controlledTicket = null;
            }
            else
            {
                _readOnlyTicket = null;
            }

            return SnapshotUnsafe();
        }
    }

    internal FeishuRouteGateState Invalidate(bool ownerBound, bool listening)
    {
        lock (_sync)
        {
            InvalidateUnsafe(listening, ownerBound);
            return SnapshotUnsafe();
        }
    }

    internal bool ExpireArmed(
        FeishuRouteKind kind,
        long expectedGeneration,
        long expectedArmVersion)
    {
        lock (_sync)
        {
            if (_disposed || expectedGeneration != _generation)
            {
                return false;
            }

            long currentVersion = kind == FeishuRouteKind.Controlled
                ? _controlledArmVersion
                : _readOnlyArmVersion;
            if (expectedArmVersion != currentVersion || !IsArmedUnsafe(kind))
            {
                return false;
            }

            if (!IsArmExpiredUnsafe(kind))
            {
                return false;
            }

            ClearGateUnsafe(kind);
            return true;
        }
    }

    internal bool ExpireAuthorization(
        FeishuRouteKind kind,
        long expectedGeneration,
        long expectedTicketVersion)
    {
        lock (_sync)
        {
            if (_disposed || expectedGeneration != _generation)
            {
                return false;
            }

            Ticket? ticket = kind == FeishuRouteKind.Controlled
                ? _controlledTicket
                : _readOnlyTicket;
            if (ticket is null
                || ticket.Version != expectedTicketVersion
                || ticket.Claimed
                || !IsTicketExpiredUnsafe(ticket))
            {
                return false;
            }

            if (kind == FeishuRouteKind.Controlled)
            {
                _controlledTicket = null;
            }
            else
            {
                _readOnlyTicket = null;
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Cancel();
            _session.Dispose();
            while (_retiredSessions.TryDequeue(out CancellationTokenSource? retired))
            {
                retired.Dispose();
            }
        }
    }

    private Ticket NewTicket(
        string messageId,
        string? prompt,
        string? projectPath,
        DateTimeOffset now,
        long version) =>
        new(
            _generation,
            messageId,
            prompt,
            projectPath ?? string.Empty,
            now,
            now + AuthorizationTtl,
            _timeProvider.GetTimestamp(),
            version);

    private Ticket ValidateAuthorizationUnsafe(FeishuRouteAuthorization authorization)
    {
        if (authorization.Generation != _generation || !_listening || !_ownerBound)
        {
            throw new InvalidOperationException("路由授权票据所属会话已失效。");
        }

        Ticket? ticket = authorization.Kind == FeishuRouteKind.Controlled
            ? _controlledTicket
            : _readOnlyTicket;
        if (ticket is null || ticket.MessageId != authorization.MessageId)
        {
            throw new InvalidOperationException("路由授权票据已清除或不匹配。");
        }

        return ticket;
    }

    private bool RememberMessageUnsafe(string messageId, DateTimeOffset now)
    {
        while (_seenOrder.TryPeek(out var oldest)
            && (oldest.SeenAt + DedupeTtl <= now || _seenMessages.Count >= DedupeCapacity))
        {
            _seenOrder.Dequeue();
            if (_seenMessages.TryGetValue(oldest.Digest, out DateTimeOffset current)
                && current == oldest.SeenAt)
            {
                _seenMessages.Remove(oldest.Digest);
            }
        }

        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(messageId)));
        if (_seenMessages.TryGetValue(digest, out DateTimeOffset seenAt)
            && seenAt + DedupeTtl > now)
        {
            return false;
        }

        _seenMessages[digest] = now;
        _seenOrder.Enqueue((digest, now));
        return true;
    }

    private void InvalidateUnsafe(bool listening, bool ownerBound)
    {
        _generation++;
        _listening = listening;
        _ownerBound = ownerBound;
        _controlledArmed = false;
        _readOnlyArmed = false;
        _controlledArmedAtTimestamp = null;
        _readOnlyArmedAtTimestamp = null;
        _controlledArmedExpiresAt = null;
        _readOnlyArmedExpiresAt = null;
        _controlledArmVersion++;
        _readOnlyArmVersion++;
        _controlledTicket = null;
        _readOnlyTicket = null;
        _session.Cancel();
        RetireSessionUnsafe(_session);
        _session = new CancellationTokenSource();
    }

    private void EnsureActiveSessionUnsafe()
    {
        if (_session.IsCancellationRequested)
        {
            RetireSessionUnsafe(_session);
            _session = new CancellationTokenSource();
        }
    }

    private void RetireSessionUnsafe(CancellationTokenSource session)
    {
        _retiredSessions.Enqueue(session);
        while (_retiredSessions.Count > RetiredSessionCapacity)
        {
            _retiredSessions.Dequeue().Dispose();
        }
    }

    private FeishuRouteGateState SnapshotUnsafe() => new(
        _generation,
        _listening,
        _ownerBound,
        _controlledArmed,
        _controlledTicket is not null,
        _controlledTicket?.Claimed == true,
        _readOnlyArmed,
        _readOnlyTicket is not null,
        _readOnlyTicket?.Claimed == true,
        _readOnlyTicket?.Prompt,
        _controlledTicket?.CapturedAt,
        _readOnlyTicket?.CapturedAt,
        _controlledArmedExpiresAt,
        _readOnlyArmedExpiresAt,
        _controlledArmVersion,
        _readOnlyArmVersion);

    private bool ExpireArmedUnsafe()
    {
        bool expired = false;
        if (_controlledArmed && IsArmExpiredUnsafe(FeishuRouteKind.Controlled))
        {
            ClearGateUnsafe(FeishuRouteKind.Controlled);
            expired = true;
        }

        if (_readOnlyArmed && IsArmExpiredUnsafe(FeishuRouteKind.ReadOnly))
        {
            ClearGateUnsafe(FeishuRouteKind.ReadOnly);
            expired = true;
        }

        return expired;
    }

    private bool IsArmExpiredUnsafe(FeishuRouteKind kind)
    {
        long? armedAt = kind == FeishuRouteKind.Controlled
            ? _controlledArmedAtTimestamp
            : _readOnlyArmedAtTimestamp;
        return armedAt is null
            || _timeProvider.GetElapsedTime(armedAt.Value, _timeProvider.GetTimestamp()) >= ArmedTtl;
    }

    private bool IsTicketExpiredUnsafe(Ticket ticket) =>
        _timeProvider.GetElapsedTime(
            ticket.CapturedAtTimestamp,
            _timeProvider.GetTimestamp()) >= AuthorizationTtl;

    private bool IsArmedUnsafe(FeishuRouteKind kind) =>
        kind == FeishuRouteKind.Controlled ? _controlledArmed : _readOnlyArmed;

    private void ClearGateUnsafe(FeishuRouteKind kind)
    {
        if (kind == FeishuRouteKind.Controlled)
        {
            _controlledArmed = false;
            _controlledArmedAtTimestamp = null;
            _controlledArmedExpiresAt = null;
            _controlledTicket = null;
            _controlledArmVersion++;
        }
        else
        {
            _readOnlyArmed = false;
            _readOnlyArmedAtTimestamp = null;
            _readOnlyArmedExpiresAt = null;
            _readOnlyTicket = null;
            _readOnlyArmVersion++;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Ticket(
        long generation,
        string messageId,
        string? prompt,
        string projectPath,
        DateTimeOffset capturedAt,
        DateTimeOffset expiresAt,
        long capturedAtTimestamp,
        long version)
    {
        internal long Generation { get; } = generation;
        internal string MessageId { get; } = messageId;
        internal string? Prompt { get; } = prompt;
        internal string ProjectPath { get; } = projectPath;
        internal DateTimeOffset CapturedAt { get; } = capturedAt;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal long CapturedAtTimestamp { get; } = capturedAtTimestamp;
        internal long Version { get; } = version;
        internal bool Claimed { get; set; }
    }
}
