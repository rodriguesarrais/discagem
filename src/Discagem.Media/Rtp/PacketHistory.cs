namespace Discagem.Media.Rtp;

/// <summary>
/// Thread-safe ring buffer of recently sent packets, indexed by media sequence number,
/// used to answer NACK requests.
/// </summary>
public sealed class PacketHistory
{
    private readonly VideoPacket?[] _slots;
    private readonly int _mask;
    private readonly object _sync = new();

    public PacketHistory(int capacity = 4096)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));
        }

        _slots = new VideoPacket?[capacity];
        _mask = capacity - 1;
    }

    public void Add(VideoPacket packet)
    {
        lock (_sync)
        {
            _slots[packet.MediaSeq & _mask] = packet;
        }
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
        }
    }
}
