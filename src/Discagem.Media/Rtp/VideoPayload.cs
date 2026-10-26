using System.Buffers.Binary;

namespace Discagem.Media.Rtp;

/// <summary>
/// Discagem video RTP payload format.
/// </summary>
/// <remarks>
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |X|R|N|S|R| PID | media sequence number (16 bits)|     flags     |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                     VP8 frame fragment ...                    |
/// </code>
/// The first byte is a valid one-byte VP8 payload descriptor (RFC 7741) so generic RTP stacks
/// can treat the stream as VP8. It is followed by a media sequence number that, unlike the RTP
/// sequence number, is preserved end-to-end through the SFU and across retransmissions. This lets
/// receivers NACK lost packets and receive retransmissions without colliding with the SRTP replay
/// window, since every retransmission travels with a fresh RTP sequence number.
/// </remarks>
public static class VideoPayload
{
    public const int HeaderSize = 4;

    /// <summary>Maximum frame bytes per packet, keeping datagrams comfortably below a 1500-byte MTU.</summary>
    public const int MaxFragmentSize = 1160;

    public const byte FlagKeyFrame = 0x01;
    public const byte FlagFrameStart = 0x02;
    public const byte FlagFrameEnd = 0x04;
    public const byte FlagRetransmission = 0x08;

    private const byte Vp8StartOfPartition = 0x10;

    public static void WriteHeader(Span<byte> destination, ushort mediaSeq, byte flags)
    {
        destination[0] = (flags & FlagFrameStart) != 0 ? Vp8StartOfPartition : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[1..], mediaSeq);
        destination[3] = flags;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> payload, out ushort mediaSeq, out byte flags)
    {
        if (payload.Length < HeaderSize)
        {
            mediaSeq = 0;
            flags = 0;
            return false;
        }

        mediaSeq = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        flags = payload[3];
        return true;
    }
}

/// <summary>A packet of the Discagem video payload format together with its RTP framing data.</summary>
public sealed record VideoPacket(ushort MediaSeq, uint Timestamp, byte Flags, byte[] Payload)
{
    public bool IsKeyFrame => (Flags & VideoPayload.FlagKeyFrame) != 0;

    public bool IsFrameStart => (Flags & VideoPayload.FlagFrameStart) != 0;

    public bool IsFrameEnd => (Flags & VideoPayload.FlagFrameEnd) != 0;

    public bool Marker => IsFrameEnd;

    public ReadOnlySpan<byte> Fragment => Payload.AsSpan(VideoPayload.HeaderSize);

    /// <summary>Parses an RTP payload, returning null when it is not a valid Discagem video payload.</summary>
    public static VideoPacket? Parse(byte[] payload, uint timestamp)
    {
        if (!VideoPayload.TryReadHeader(payload, out var seq, out var flags))
        {
            return null;
        }

        return new VideoPacket(seq, timestamp, flags, payload);
    }
}

/// <summary>Converts 16-bit sequence numbers into monotonically comparable 64-bit values.</summary>
public sealed class SequenceUnwrapper
{
    private const long Base = 1L << 32;
    private long _last = -1;

    public long Unwrap(ushort seq)
    {
        if (_last < 0)
        {
            _last = Base + seq;
            return _last;
        }

        var candidate = (_last & ~0xFFFFL) | seq;
        if (candidate < _last - 0x8000)
        {
            candidate += 0x10000;
        }
        else if (candidate > _last + 0x8000)
        {
            candidate -= 0x10000;
        }

        if (candidate > _last)
        {
            _last = candidate;
        }

        return candidate;
    }

    public void Reset() => _last = -1;
}
