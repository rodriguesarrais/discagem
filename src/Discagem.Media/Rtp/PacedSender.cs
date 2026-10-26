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
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopped;
    private long _queuedBytes;

    public PacedSender(Action<VideoPacket> send, int rateKbps)
    {
        _send = send;
        RateKbps = rateKbps;
        _thread = new Thread(Run) { IsBackground = true, Name = "Discagem pacer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Pacing rate. Should be comfortably above the encoder bitrate.</summary>
    public int RateKbps { get; set; }

    /// <summary>Raised (on the pacer thread) when the queue overflowed and was flushed.</summary>
    public event Action? Overflowed;

    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

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

            // More than ~1.5 s of backlog: the link cannot keep up. Flush and let the caller send a key frame.
            if (_queuedBytes > (long)RateKbps * 1000 / 8 * 3 / 2 && _media.Count > 0)
            {
                _media.Clear();
                _queuedBytes = _retransmissions.Sum(p => (long)p.Payload.Length);
                overflow = true;
            }
        }

        _signal.Set();
        if (overflow)
        {
            Overflowed?.Invoke();
        }
    }

    public void EnqueueRetransmission(VideoPacket packet)
    {
        lock (_sync)
        {
            _retransmissions.Enqueue(packet);
            _queuedBytes += packet.Payload.Length;
        }

        _signal.Set();
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
                    if (!_retransmissions.TryDequeue(out packet) && !_media.TryDequeue(out packet))
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

            bool empty;
            lock (_sync)
            {
                empty = _media.Count == 0 && _retransmissions.Count == 0;
            }

            if (empty)
            {
                tokens = Math.Max(tokens, 0);
                _signal.WaitOne(20);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _signal.Dispose();
    }
}
