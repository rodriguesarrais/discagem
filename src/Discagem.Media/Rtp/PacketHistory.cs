namespace Discagem.Media.Rtp;

/// <summary>
/// Thread-safe ring buffer of recently sent packets, indexed by media sequence number,
/// used to answer NACK requests.
/// </summary>
public sealed class PacketHistory
{
    private readonly VideoPacket?[] _slots;
    private readonly long[] _lastResentMs;
    private readonly int _mask;
    private readonly object _sync = new();

    public PacketHistory(int capacity = 4096)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));
        }

        _slots = new VideoPacket?[capacity];
        _lastResentMs = new long[capacity];
        Array.Fill(_lastResentMs, long.MinValue);
        _mask = capacity - 1;
    }

    public void Add(VideoPacket packet)
    {
        lock (_sync)
        {
            _slots[packet.MediaSeq & _mask] = packet;
            _lastResentMs[packet.MediaSeq & _mask] = long.MinValue;
        }
    }

    /// <summary>
    /// Looks up a packet for retransmission. Returns false when it is unknown, or when it was already
    /// retransmitted less than <paramref name="minIntervalMs"/> ago (a duplicate NACK that crossed the
    /// first retransmission in flight). <paramref name="isFirstRequest"/> tells whether this is the
    /// first time the packet was requested.
    /// </summary>
    public bool TryGetForResend(ushort mediaSeq, long nowMs, int minIntervalMs, out VideoPacket packet, out bool isFirstRequest)
    {
        lock (_sync)
        {
            var slot = mediaSeq & _mask;
            var candidate = _slots[slot];
            if (candidate is not null && candidate.MediaSeq == mediaSeq)
            {
                var last = _lastResentMs[slot];
                isFirstRequest = last == long.MinValue;
                if (!isFirstRequest && nowMs - last < minIntervalMs)
                {
                    packet = null!;
                    return false;
                }

                _lastResentMs[slot] = nowMs;
                packet = candidate;
                return true;
            }
        }

        packet = null!;
        isFirstRequest = false;
        return false;
    }

    public bool TryGet(ushort mediaSeq, out VideoPacket packet)
    {
        lock (_sync)
        {
            var candidate = _slots[mediaSeq & _mask];
            if (candidate is not null && candidate.MediaSeq == mediaSeq)
            {
                packet = candidate;
                return true;
            }
        }

        packet = null!;
        return false;
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_slots);
            Array.Fill(_lastResentMs, long.MinValue);
        }
    }
}
