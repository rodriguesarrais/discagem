namespace Discagem.Media.Rtp;

/// <summary>
/// Detects gaps in a stream of media sequence numbers and decides when to (re)request
/// the missing packets with NACKs.
/// </summary>
public sealed class NackTracker
{
    private sealed class Entry
    {
        public long DetectedAtMs;
        public long FirstSentMs;
        public long LastSentMs = long.MinValue;
        public int Retries;
    }

    private readonly object _sync = new();
    private readonly SequenceUnwrapper _unwrapper = new();
    private readonly SortedDictionary<long, Entry> _missing = new();
    private long _highest = -1;
    private double _smoothedRttMs = double.NaN;

    /// <summary>Delay before the first NACK, to tolerate mild reordering.</summary>
    public int InitialDelayMs { get; init; } = 5;

    /// <summary>
    /// Interval between repeated NACKs for the same packet until a round trip has been measured.
    /// Afterwards the interval follows the measured round trip (see <see cref="CurrentRetryIntervalMs"/>),
    /// so that a packet is not requested again while its retransmission is still in flight.
    /// </summary>
    public int RetryIntervalMs { get; set; } = 100;

    public int MinRetryIntervalMs { get; init; } = 40;

    public int MaxRetryIntervalMs { get; init; } = 600;

    /// <summary>Smoothed time between the first NACK for a packet and its arrival, if measured yet.</summary>
    public double? RoundTripMs
    {
        get
        {
            lock (_sync)
            {
                return double.IsNaN(_smoothedRttMs) ? null : _smoothedRttMs;
            }
        }
    }

    public int CurrentRetryIntervalMs
    {
        get
        {
            lock (_sync)
            {
                return RetryInterval();
            }
        }
    }

    private int RetryInterval() => double.IsNaN(_smoothedRttMs)
        ? RetryIntervalMs
        : Math.Clamp((int)(_smoothedRttMs * 1.5) + 10, MinRetryIntervalMs, MaxRetryIntervalMs);

    public int MaxRetries { get; init; } = 6;

    /// <summary>Gaps larger than this are treated as a discontinuity rather than loss.</summary>
    public int MaxGap { get; init; } = 1500;

    public int MaxTracked { get; init; } = 2000;

    public int MissingCount
    {
        get
        {
            lock (_sync)
            {
                return _missing.Count;
            }
        }
    }

    public void OnPacket(ushort seq, long nowMs)
    {
        lock (_sync)
        {
            var ext = _unwrapper.Unwrap(seq);
            if (_highest < 0)
            {
                _highest = ext;
                return;
            }

            if (ext > _highest)
            {
                var gap = ext - _highest - 1;
                if (gap > MaxGap)
                {
                    _missing.Clear();
                }
                else
                {
                    for (var s = _highest + 1; s < ext; s++)
                    {
                        _missing[s] = new Entry { DetectedAtMs = nowMs };
                    }

                    while (_missing.Count > MaxTracked)
                    {
                        _missing.Remove(_missing.Keys.First());
                    }
                }

                _highest = ext;
            }
            else if (_missing.Remove(ext, out var entry) && entry.Retries > 0)
            {
                // A retransmission. Measuring from the first request errs on the long side when that
                // request was lost, which only makes repeats rarer.
                var sample = Math.Clamp(nowMs - entry.FirstSentMs, 1, 5000);
                _smoothedRttMs = double.IsNaN(_smoothedRttMs) ? sample : _smoothedRttMs * 0.875 + sample * 0.125;
            }
        }
    }

    /// <summary>Returns the sequence numbers that should be NACKed now (in ascending order).</summary>
    public List<ushort> CollectNacks(long nowMs)
    {
        var result = new List<ushort>();
        lock (_sync)
        {
            if (_missing.Count == 0)
            {
                return result;
            }

            List<long>? expired = null;
            var retryInterval = RetryInterval();
            foreach (var (ext, entry) in _missing)
            {
                if (entry.Retries >= MaxRetries || ext < _highest - MaxGap * 2)
                {
                    (expired ??= []).Add(ext);
                    continue;
                }

                var due = entry.Retries == 0
                    ? nowMs - entry.DetectedAtMs >= InitialDelayMs
                    : nowMs - entry.LastSentMs >= retryInterval;
                if (!due)
                {
                    continue;
                }

                if (entry.Retries == 0)
                {
                    entry.FirstSentMs = nowMs;
                }

                entry.Retries++;
                entry.LastSentMs = nowMs;
                result.Add((ushort)(ext & 0xFFFF));
            }

            if (expired is not null)
            {
                foreach (var ext in expired)
                {
                    _missing.Remove(ext);
                }
            }
        }

        return result;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _missing.Clear();
            _unwrapper.Reset();
            _highest = -1;
            _smoothedRttMs = double.NaN;
        }
    }
}
