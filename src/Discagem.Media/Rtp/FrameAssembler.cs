namespace Discagem.Media.Rtp;

/// <summary>A complete encoded frame reassembled from packets.</summary>
public readonly record struct AssembledFrame(byte[] Data, uint Timestamp, bool IsKeyFrame);

/// <summary>
/// Receiver-side jitter buffer: reorders packets by media sequence number, waits for retransmissions
/// of lost packets, and emits complete frames in decode order. After unrecoverable loss it discards
/// frames until the next key frame and signals that a key frame should be requested.
/// </summary>
/// <remarks>Not thread-safe; callers serialise access.</remarks>
public sealed class FrameAssembler
{
    private readonly SequenceUnwrapper _unwrapper = new();
    private readonly SortedDictionary<long, VideoPacket> _buffer = new();
    private readonly SortedSet<long> _keyFrameStarts = new();
    private long _nextSeq = -1;
    private long _lowestAccepted = long.MinValue;
    private bool _waitingForKeyFrame = true;
    private long _blockedSinceMs = -1;
    private long _lastKeyFrameRequestMs = long.MinValue / 2;

    /// <summary>How long to wait for a missing packet before giving up and waiting for a key frame.</summary>
    public int GiveUpMs { get; init; } = 700;

    public int MaxBufferedPackets { get; init; } = 8000;

    public int MaxPacketsPerFrame { get; init; } = 4000;

    /// <summary>Minimum interval between key frame requests.</summary>
    public int KeyFrameRequestIntervalMs { get; init; } = 1000;

    public bool IsWaitingForKeyFrame => _waitingForKeyFrame;

    public int BufferedPackets => _buffer.Count;

    public long FramesDropped { get; private set; }

    /// <summary>Number of times the assembler gave up waiting for a missing packet.</summary>
    public long GiveUps { get; private set; }

    /// <summary>Number of times the assembler skipped ahead to an already complete key frame.</summary>
    public long KeyFrameJumps { get; private set; }

    /// <summary>The sequence number that was missing at the last give-up (diagnostics).</summary>
    public long LastMissingSeq { get; private set; } = -1;

    public void Insert(VideoPacket packet, long nowMs, List<AssembledFrame> output)
    {
        var ext = _unwrapper.Unwrap(packet.MediaSeq);
        if (ext < _lowestAccepted || _buffer.ContainsKey(ext))
        {
            return;
        }

        _buffer[ext] = packet;
        if (packet.IsFrameStart && packet.IsKeyFrame)
        {
            _keyFrameStarts.Add(ext);
        }

        if (_buffer.Count > MaxBufferedPackets)
        {
            Clear();
            EnterWaitingForKeyFrame();
            return;
        }

        Drain(nowMs, output);
    }

    /// <summary>Periodic maintenance: applies the give-up timeout.</summary>
    public void Tick(long nowMs, List<AssembledFrame> output)
    {
        Drain(nowMs, output);
        if (!_waitingForKeyFrame && _blockedSinceMs >= 0 && nowMs - _blockedSinceMs > GiveUpMs)
        {
            FramesDropped++;
            GiveUps++;
            LastMissingSeq = FirstMissing();
            EnterWaitingForKeyFrame();
            Drain(nowMs, output);
        }
    }

    /// <summary>Returns true (at most once per interval) when a key frame request should be sent.</summary>
    public bool ShouldRequestKeyFrame(long nowMs)
    {
        if (!_waitingForKeyFrame || nowMs - _lastKeyFrameRequestMs < KeyFrameRequestIntervalMs)
        {
            return false;
        }

        _lastKeyFrameRequestMs = nowMs;
        return true;
    }

    /// <summary>Called when the decoder failed: drop everything until the next key frame.</summary>
    public void OnDecodeError() => EnterWaitingForKeyFrame();

    private void EnterWaitingForKeyFrame()
    {
        _waitingForKeyFrame = true;
        _blockedSinceMs = -1;
    }

    private long FirstMissing()
    {
        for (var s = _nextSeq; s < _nextSeq + MaxPacketsPerFrame; s++)
        {
            if (!_buffer.ContainsKey(s))
            {
                return s & 0xFFFF;
            }
        }

        return -1;
    }

    private void Clear()
    {
        _buffer.Clear();
        _keyFrameStarts.Clear();
    }

    private void Drain(long nowMs, List<AssembledFrame> output)
    {
        while (true)
        {
            if (_waitingForKeyFrame)
            {
                if (!TryTakeCompleteKeyFrame(output))
                {
                    return;
                }

                continue;
            }

            if (_buffer.Count == 0)
            {
                _blockedSinceMs = -1;
                return;
            }

            if (_buffer.TryGetValue(_nextSeq, out var first) && first.IsFrameStart &&
                TryCollect(_nextSeq, out var frame, out var end))
            {
                Consume(end);
                output.Add(frame);
                continue;
            }

            // Blocked on a missing packet. If a complete key frame is already available further
            // ahead there is no point in waiting for retransmissions.
            if (TryTakeCompleteKeyFrame(output))
            {
                FramesDropped++;
                KeyFrameJumps++;
                continue;
            }

            if (_blockedSinceMs < 0)
            {
                _blockedSinceMs = nowMs;
            }

            return;
        }
    }

    private bool TryTakeCompleteKeyFrame(List<AssembledFrame> output)
    {
        AssembledFrame? found = null;
        long foundEnd = -1;
        foreach (var start in _keyFrameStarts)
        {
            if (start >= _lowestAccepted && TryCollect(start, out var frame, out var end))
            {
                found = frame;
                foundEnd = end;
                break;
            }
        }

        if (found is null)
        {
            return false;
        }

        Consume(foundEnd);
        _waitingForKeyFrame = false;
        output.Add(found.Value);
        return true;
    }

    private void Consume(long endExt)
    {
        while (_buffer.Count > 0)
        {
            var firstKey = _buffer.Keys.First();
            if (firstKey > endExt)
            {
                break;
            }

            _buffer.Remove(firstKey);
        }

        while (_keyFrameStarts.Count > 0 && _keyFrameStarts.Min <= endExt)
        {
            _keyFrameStarts.Remove(_keyFrameStarts.Min);
        }

        _nextSeq = endExt + 1;
        _lowestAccepted = _nextSeq;
        _blockedSinceMs = -1;
    }

    private bool TryCollect(long startExt, out AssembledFrame frame, out long endExt)
    {
        frame = default;
        endExt = -1;
        if (!_buffer.TryGetValue(startExt, out var startPacket) || !startPacket.IsFrameStart)
        {
            return false;
        }

        var timestamp = startPacket.Timestamp;
        var totalLength = 0;
        var count = 0;
        for (var s = startExt; ; s++)
        {
            if (!_buffer.TryGetValue(s, out var packet) || packet.Timestamp != timestamp)
            {
                return false;
            }

            if (s != startExt && packet.IsFrameStart)
            {
                return false;
            }

            totalLength += packet.Payload.Length - VideoPayload.HeaderSize;
            count++;
            if (packet.IsFrameEnd)
            {
                endExt = s;
                break;
            }

            if (count >= MaxPacketsPerFrame)
            {
                return false;
            }
        }

        var data = new byte[totalLength];
        var offset = 0;
        for (var s = startExt; s <= endExt; s++)
        {
            var fragment = _buffer[s].Fragment;
            fragment.CopyTo(data.AsSpan(offset));
            offset += fragment.Length;
        }

        frame = new AssembledFrame(data, timestamp, startPacket.IsKeyFrame);
        return true;
    }
}
