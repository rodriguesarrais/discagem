using Discagem.Media.Rtp;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>
/// One remote speaker: an adaptive jitter buffer in front of an Opus decoder. Frames are pulled at
/// the pace of the playback device; lost frames are rebuilt from the next packet's forward error
/// correction data when possible, and concealed otherwise.
/// </summary>
/// <remarks>
/// Speakers only send while they talk, so a talk spurt simply stops: after a few concealed frames
/// with nothing buffered the stream goes idle, and the next spurt is buffered afresh.
/// </remarks>
public sealed class SpeakerStream : IDisposable
{
    private const int FrameMs = DiscagemAudio.FrameMilliseconds;
    private const int MaxBufferedPackets = 50;
    private const int MaxConcealedFrames = 3;
    private const int SpeakingHoldMs = 250;

    private readonly object _sync = new();
    private readonly SortedDictionary<long, byte[]> _packets = new();
    private readonly SequenceUnwrapper _unwrapper = new();
    private readonly OpusVoiceDecoder _decoder = new();
    private long _next = -1;
    private long _lastPlayed = -1;
    private long _bufferingSinceMs = -1;
    private int _concealed;
    private long _lastArrivalMs = -1;
    private long _lastArrivalSequence = -1;
    private double _jitterMs;
    private long _lastAudibleMs = long.MinValue / 2;
    private bool _disposed;

    public SpeakerStream(uint speakerId)
    {
        SpeakerId = speakerId;
    }

    public uint SpeakerId { get; }

    /// <summary>Playback volume for this speaker (1 = unchanged).</summary>
    public float Gain { get; set; } = 1f;

    public long PacketsReceived { get; private set; }

    public long FramesDecoded { get; private set; }

    public long FramesRecovered { get; private set; }

    public long FramesConcealed { get; private set; }

    public long FramesSkipped { get; private set; }

    public long LatePackets { get; private set; }

    public double JitterMs
    {
        get
        {
            lock (_sync)
            {
                return _jitterMs;
            }
        }
    }

    /// <summary>Frames to buffer before a talk spurt starts playing (about twice the jitter, at least 40 ms).</summary>
    public int TargetFrames
    {
        get
        {
            lock (_sync)
            {
                return Target();
            }
        }
    }

    public int BufferedPackets
    {
        get
        {
            lock (_sync)
            {
                return _packets.Count;
            }
        }
    }

    private int Target() => Math.Clamp((int)Math.Ceiling((_jitterMs * 2 + 10) / FrameMs), 2, 10);

    public bool IsSpeaking(long nowMs)
    {
        lock (_sync)
        {
            return nowMs - _lastAudibleMs < SpeakingHoldMs;
        }
    }

    public void Push(ushort sequence, ReadOnlySpan<byte> opus, long nowMs)
    {
        if (opus.IsEmpty || opus.Length > OpusVoiceEncoder.MaxPacketBytes)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            PacketsReceived++;
            var extended = _unwrapper.Unwrap(sequence);
            if (extended <= _lastPlayed)
            {
                if (_lastPlayed - extended < MaxBufferedPackets * 4)
                {
                    LatePackets++;
                    return;
                }

                // A large step backwards: the speaker's client restarted its stream.
                Restart();
                extended = _unwrapper.Unwrap(sequence);
            }

            UpdateJitter(extended, nowMs);
            if (_next >= 0 && extended >= _next + MaxBufferedPackets)
            {
                // Far ahead of the playout point: resynchronise rather than play stale audio.
                _packets.Clear();
                _next = -1;
            }

            _packets.TryAdd(extended, opus.ToArray());
            while (_packets.Count > MaxBufferedPackets)
            {
                _packets.Remove(_packets.Keys.First());
            }

            if (_next < 0 && _bufferingSinceMs < 0)
            {
                _bufferingSinceMs = nowMs;
            }
        }
    }

    private void Restart()
    {
        _packets.Clear();
        _unwrapper.Reset();
        _next = -1;
        _lastPlayed = -1;
        _bufferingSinceMs = -1;
        _lastArrivalMs = -1;
        _lastArrivalSequence = -1;
    }

    /// <summary>Interarrival jitter in the spirit of RFC 3550 (section 6.4.1), using the 20 ms packet clock.</summary>
    private void UpdateJitter(long extended, long nowMs)
    {
        if (_lastArrivalMs >= 0 && extended > _lastArrivalSequence)
        {
            var expected = (extended - _lastArrivalSequence) * FrameMs;
            var deviation = nowMs - _lastArrivalMs - expected;

            // A long pause is the speaker going quiet, not network jitter.
            if (deviation < 300)
            {
                _jitterMs += (Math.Abs(deviation) - _jitterMs) / 16;
            }
        }

        if (extended > _lastArrivalSequence)
        {
            _lastArrivalMs = nowMs;
            _lastArrivalSequence = extended;
        }
    }

    /// <summary>
    /// Produces the next 20 ms frame into <paramref name="frame"/>. Returns false (and leaves the
    /// frame unspecified) when the speaker is silent.
    /// </summary>
    public bool Read(Span<float> frame, long nowMs)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            if (_next < 0)
            {
                if (_packets.Count == 0)
                {
                    return false;
                }

                // Wait for enough packets to absorb the jitter, but do not hold back a short spurt forever.
                var target = Target();
                if (_packets.Count < target && nowMs - _bufferingSinceMs < target * FrameMs)
                {
                    return false;
                }

                _next = _packets.Keys.First();
                _bufferingSinceMs = -1;
                _concealed = 0;
            }

            // Too much queued (a burst after a stall, or clock drift between the two ends): drop a frame
            // to bring the latency back down.
            if (_packets.Count > Target() + 3)
            {
                var oldest = _packets.Keys.First();
                _packets.Remove(oldest);
                _next = Math.Max(_next, oldest + 1);
                FramesSkipped++;
            }

            if (_packets.Remove(_next, out var packet))
            {
                if (_decoder.Decode(packet, frame))
                {
                    FramesDecoded++;
                    _lastAudibleMs = nowMs;
                }
                else
                {
                    _decoder.Conceal(frame);
                }

                _concealed = 0;
            }
            else if (_packets.TryGetValue(_next + 1, out var following) && _decoder.DecodeFec(following, frame))
            {
                FramesRecovered++;
                _concealed = 0;
            }
            else if (_packets.Count > 0 || _concealed < MaxConcealedFrames)
            {
                _decoder.Conceal(frame);
                FramesConcealed++;
                _concealed++;

                // A long gap with later packets waiting: jump to them instead of concealing for ages.
                if (_packets.Count > 0 && _concealed >= MaxConcealedFrames)
                {
                    _lastPlayed = _packets.Keys.First() - 1;
                    _next = _lastPlayed + 1;
                    _concealed = 0;
                    ApplyGain(frame);
                    return true;
                }
            }
            else
            {
                // Nothing left: the speaker stopped talking. The concealed tail did not consume any real
                // packets: the sender's sequence numbers carry on without a gap in the next talk spurt.
                _lastPlayed = _next - 1 - _concealed;
                _next = -1;
                return false;
            }

            _lastPlayed = _next;
            _next++;
            ApplyGain(frame);
            return true;
        }
    }

    private void ApplyGain(Span<float> frame)
    {
        var gain = Gain;
        if (gain == 1f)
        {
            return;
        }

        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] *= gain;
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

            // The mixer may still hold this stream in its snapshot; Read checks the flag under the same lock.
            _disposed = true;
            _packets.Clear();
            _decoder.Dispose();
        }
    }
}
