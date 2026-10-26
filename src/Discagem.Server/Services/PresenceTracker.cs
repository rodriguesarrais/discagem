using Microsoft.AspNetCore.SignalR;

namespace Discagem.Server.Services;

/// <summary>Tracks which users are online, accounting for several simultaneous connections per user.</summary>
public sealed class PresenceTracker
{
    private readonly Dictionary<long, Dictionary<string, HubCallerContext>> _connections = new();
    private readonly object _sync = new();

    /// <summary>Registers a connection. Returns true if the user just came online.</summary>
    public bool Connect(long userId, HubCallerContext context)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                set = [];
                _connections[userId] = set;
            }

            set[context.ConnectionId] = context;
            return set.Count == 1;
        }
    }

    /// <summary>Removes a connection. Returns true if the user went offline.</summary>
    public bool Disconnect(long userId, string connectionId)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                return false;
            }

            set.Remove(connectionId);
            if (set.Count > 0)
            {
                return false;
            }

            _connections.Remove(userId);
            return true;
        }
    }

    /// <summary>
    /// Closes the user's hub connections (for example after a password change, so that a stolen token
    /// cannot keep an already open connection alive). Returns the number of connections closed.
    /// </summary>
    public int AbortConnections(long userId, string? exceptConnectionId = null)
    {
        List<HubCallerContext> contexts;
        lock (_sync)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                return 0;
            }

            contexts = set.Values.Where(c => c.ConnectionId != exceptConnectionId).ToList();
        }

        foreach (var context in contexts)
        {
            context.Abort();
        }

        return contexts.Count;
    }

    public IReadOnlyList<long> OnlineUserIds()
    {
        lock (_sync)
        {
            return _connections.Keys.ToList();
        }
    }

    public bool IsOnline(long userId)
    {
        lock (_sync)
        {
            return _connections.ContainsKey(userId);
        }
    }
}
