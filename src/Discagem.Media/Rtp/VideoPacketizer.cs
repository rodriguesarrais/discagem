namespace Discagem.Media.Rtp;

/// <summary>Splits encoded frames into <see cref="VideoPacket"/>s with consecutive media sequence numbers.</summary>
public sealed class VideoPacketizer
{
    private ushort _nextSeq;

    public VideoPacketizer(ushort? initialSequence = null)
    {
        _nextSeq = initialSequence ?? (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
    }

    public List<VideoPacket> Packetize(ReadOnlySpan<byte> frame, uint timestamp, bool isKeyFrame)
    {
        if (frame.IsEmpty)
        {
            return [];
        }

        var count = (frame.Length + VideoPayload.MaxFragmentSize - 1) / VideoPayload.MaxFragmentSize;
        var packets = new List<VideoPacket>(count);

        for (var i = 0; i < count; i++)
        {
            // Balance fragment sizes so the last packet is not tiny.
            var offset = (int)((long)frame.Length * i / count);
            var length = (int)((long)frame.Length * (i + 1) / count) - offset;
            byte flags = 0;
            if (isKeyFrame)
            {
                flags |= VideoPayload.FlagKeyFrame;
            }

            if (i == 0)
            {
                flags |= VideoPayload.FlagFrameStart;
            }

            if (i == count - 1)
            {
                flags |= VideoPayload.FlagFrameEnd;
            }

            var payload = new byte[VideoPayload.HeaderSize + length];
            var seq = _nextSeq++;
            VideoPayload.WriteHeader(payload, seq, flags);
            frame.Slice(offset, length).CopyTo(payload.AsSpan(VideoPayload.HeaderSize));
            packets.Add(new VideoPacket(seq, timestamp, flags, payload));
        }

        return packets;
    }
}
