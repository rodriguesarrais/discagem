using System.Diagnostics;

namespace Discagem.Media.Rtp;

/// <summary>
/// Spreads outgoing packets over time with a token bucket so that large key frames do not leave
/// as a single burst (which home routers and Wi-Fi links tend to drop).
/// Retransmissions are sent ahead of new media.
/// </summary>
public sealed class PacedSender : IDisposable
{
    private readonly Action<VideoPacket> _send;
    private readonly Queue<VideoPacket> _media = new();
    private readonly Queue<VideoPacket> _retransmissions = new();
    private readonly object _sync = new();
    private readonly Thread _thread;
    private volatile bool _stopped;
    private long _queuedBytes;
    private long _retransmissionBytes;

    public PacedSender(Action<VideoPacket> send, int rateKbps)
    {
        _send = send;
        RateKbps = rateKbps;
        _thread = new Thread(Run) { IsBackground = true, Name = "Discagem pacer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Pacing rate. Should be comfortably above the encoder bitrate.</summary>
    public int RateKbps { get; set; }

    /// <summary>Raised (on the thread that queued the frame) when the queue overflowed and was flushed.</summary>
    public event Action? Overflowed;

    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    private long BytesPerSecond => (long)Math.Max(RateKbps, 500) * 1000 / 8;

    public void Enqueue(IEnumerable<VideoPacket> packets)
    {
        var overflow = false;
        lock (_sync)
        {
            foreach (var packet in packets)
            {
                _media.Enqueue(packet);
                _queuedBytes += packet.Payload.Length;
            }

            // More than ~1.5 s of backlog: the link cannot keep up. Flush everything (pending
            // retransmissions belong to frames that are now useless too) and let the caller send a key frame.
            if (_queuedBytes > BytesPerSecond * 3 / 2)
            {
                _media.Clear();
                _retransmissions.Clear();
                _queuedBytes = 0;
                _retransmissionBytes = 0;
                overflow = true;
            }

            Monitor.Pulse(_sync);
        }

        if (overflow)
        {
            Overflowed?.Invoke();
        }
    }

    /// <summary>Queues a retransmission ahead of new media. Returns false when the retransmission backlog is full.</summary>
    public bool EnqueueRetransmission(VideoPacket packet)
    {
        lock (_sync)
        {
            // Retransmissions older than ~300 ms of backlog would arrive too late to be useful and only
            // delay live media.
            if (_retransmissionBytes + packet.Payload.Length > BytesPerSecond * 3 / 10)
            {
                return false;
            }

            _retransmissions.Enqueue(packet);
            _queuedBytes += packet.Payload.Length;
            _retransmissionBytes += packet.Payload.Length;
            Monitor.Pulse(_sync);
        }

        return true;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double tokens = 0;
        var last = clock.Elapsed.TotalSeconds;

        while (!_stopped)
        {
            var now = clock.Elapsed.TotalSeconds;
            var bytesPerSecond = Math.Max(RateKbps, 500) * 1000.0 / 8.0;
            tokens = Math.Min(tokens + (now - last) * bytesPerSecond, bytesPerSecond * 0.04);
            last = now;

            while (tokens > 0)
            {
                VideoPacket? packet;
                lock (_sync)
                {
                    if (_retransmissions.TryDequeue(out packet))
                    {
                        _retransmissionBytes -= packet.Payload.Length;
                    }
                    else if (!_media.TryDequeue(out packet))
                    {
                        break;
                    }

                    _queuedBytes -= packet.Payload.Length;
                }

                tokens -= packet.Payload.Length;
                try
                {
                    _send(packet);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Pacer send failed: {ex.Message}");
                }
            }

            var empty = false;
            lock (_sync)
            {
                if (_media.Count == 0 && _retransmissions.Count == 0 && !_stopped)
                {
                    empty = true;
                    Monitor.Wait(_sync, 20);
                }
            }

            if (empty)
            {
                tokens = Math.Max(tokens, 0);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stopped = true;
            _media.Clear();
            _retransmissions.Clear();
            Monitor.Pulse(_sync);
        }

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(1));
        }
    }
}
