using System.Diagnostics;

namespace Discagem.Server.Media;

/// <summary>
/// Token bucket limiting how many packets one sender can make the server fan out. Not thread-safe:
/// use it only from the sender's receive thread.
/// </summary>
public sealed class PacketRateLimiter(double packetsPerSecond, double burst)
{
    private readonly double _burst = burst;
    private double _tokens = burst;
    private long _lastRefill = Stopwatch.GetTimestamp();

    public bool TryConsume()
    {
        var now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(_burst, _tokens + Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds * packetsPerSecond);
        _lastRefill = now;
        if (_tokens < 1)
        {
            return false;
        }

        _tokens--;
        return true;
    }
}
