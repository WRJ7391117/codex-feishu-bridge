using System.Security.Cryptography;

namespace DeepOriBridge.Services;

internal sealed record FeishuOwnerBindingGateTicket(
    string Generation,
    string Code,
    DateTimeOffset ExpiresAt);

internal sealed class FeishuOwnerBindingGate
{
    private readonly object _sync = new();
    private FeishuOwnerBindingGateTicket? _active;

    internal FeishuOwnerBindingGateTicket Arm(string code, DateTimeOffset expiresAt)
    {
        if (code is not { Length: 6 }
            || !code.All(character => character is >= '0' and <= '9'))
        {
            throw new ArgumentException("Owner binding code must be six ASCII digits.", nameof(code));
        }

        var ticket = new FeishuOwnerBindingGateTicket(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            code,
            expiresAt);
        lock (_sync)
        {
            _active = ticket;
        }

        return ticket;
    }

    internal void Clear(Action? clearAction = null)
    {
        lock (_sync)
        {
            _active = null;
            clearAction?.Invoke();
        }
    }

    internal bool TryCommit(
        FeishuOwnerBindingGateTicket ticket,
        DateTimeOffset receivedAt,
        DateTimeOffset commitAt,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_sync)
        {
            if (!IsCurrent(ticket)
                || ticket.ExpiresAt <= receivedAt
                || ticket.ExpiresAt <= commitAt)
            {
                return false;
            }

            _active = null;
            commit();
            return true;
        }
    }

    internal bool TryExpire(
        FeishuOwnerBindingGateTicket ticket,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!IsCurrent(ticket) || ticket.ExpiresAt > now)
            {
                return false;
            }

            _active = null;
            return true;
        }
    }

    private bool IsCurrent(FeishuOwnerBindingGateTicket ticket) =>
        _active is FeishuOwnerBindingGateTicket active
        && string.Equals(active.Generation, ticket.Generation, StringComparison.Ordinal)
        && string.Equals(active.Code, ticket.Code, StringComparison.Ordinal)
        && active.ExpiresAt == ticket.ExpiresAt;
}
