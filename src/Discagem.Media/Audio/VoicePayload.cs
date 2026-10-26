using System.Buffers.Binary;

namespace Discagem.Media.Audio;

/// <summary>
/// Framing for voice packets forwarded by the server.
/// </summary>
/// <remarks>
/// Clients send plain Opus packets. The server forwards every speaker's packets to the other
/// participants on a single RTP stream, prefixing each with this header so receivers can keep a
/// jitter buffer and a decoder per speaker:
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |   version=1   |     flags     |   speaker's RTP sequence no.  |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                        speaker user id                        |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                         Opus packet ...                       |
/// </code>
/// The RTP timestamp of a forwarded packet is the speaker's original timestamp.
/// </remarks>
public static class VoicePayload
{
    public const int HeaderSize = 8;
    public const byte Version = 1;

    public static byte[] Wrap(ReadOnlySpan<byte> opus, ushort sequence, uint speakerId)
    {
        var payload = new byte[HeaderSize + opus.Length];
        payload[0] = Version;
        payload[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), speakerId);
        opus.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    public static bool TryUnwrap(byte[] payload, out ushort sequence, out uint speakerId, out ReadOnlyMemory<byte> opus)
    {
        sequence = 0;
        speakerId = 0;
        opus = default;
        if (payload.Length <= HeaderSize || payload[0] != Version)
        {
            return false;
        }

        sequence = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
        speakerId = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        opus = payload.AsMemory(HeaderSize);
        return true;
    }
}
