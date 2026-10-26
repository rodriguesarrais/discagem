namespace Discagem.Server.Services;

/// <summary>Tracks which users are online, accounting for several simultaneous connections per user.</summary>
public sealed class PresenceTracker
{
    private readonly Dictionary<long, HashSet<string>> _connections = new();
    private readonly object _sync = new();

    /// <summary>Registers a connection. Returns true if the user just came online.</summary>
    public bool Connect(long userId, string connectionId)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                set = [];
                _connections[userId] = set;
            }

            set.Add(connectionId);
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
