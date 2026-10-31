using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>Encodes 20 ms frames of 48 kHz interleaved stereo (game, video or music audio) with Opus.</summary>
public sealed class StreamAudioEncoder : IDisposable
{
    public const int Channels = 2;

    /// <summary>Samples in one frame (both channels).</summary>
    public const int FrameLength = DiscagemAudio.FrameSamples * Channels;

    private readonly IOpusEncoder _encoder;

    public StreamAudioEncoder(int bitrate = 128_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(DiscagemAudio.SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrate;
        _encoder.UseVBR = true;
        _encoder.Complexity = 7;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
    }

    /// <summary>Encodes one frame of <see cref="FrameLength"/> samples. Returns the packet length.</summary>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        if (frame.Length != FrameLength)
        {
            throw new ArgumentException($"Expected {FrameLength} samples.", nameof(frame));
        }

        return _encoder.Encode(frame, DiscagemAudio.FrameSamples, packet, packet.Length);
    }

    public void Dispose() => (_encoder as IDisposable)?.Dispose();
}

/// <summary>
/// Turns the audio captured alongside a screen share (48 kHz interleaved stereo, in blocks of any
/// size) into Opus packets. Long silences are not sent: receivers treat the gap like the end of a
/// talk spurt and buffer afresh when the sound comes back.
/// </summary>
public sealed class StreamAudioSender : IDisposable
{
    /// <summary>Frames whose peak stays below this (about -80 dBFS) count as silent.</summary>
    private const float SilencePeak = 1e-4f;

    /// <summary>Silent frames still sent before pausing, so that sounds fade out naturally.</summary>
    private const int SilentFramesBeforePause = 25;

    private readonly object _sync = new();
    private readonly StreamAudioEncoder _encoder;
    private readonly OpusPacketHandler _send;
    private readonly float[] _frame = new float[StreamAudioEncoder.FrameLength];
    private readonly byte[] _packet = new byte[OpusVoiceEncoder.MaxPacketBytes];
    private int _filled;
    private uint _timestamp;
    private int _silentFrames;
    private long _packetsSent;
    private long _framesSkipped;
    private bool _disposed;

    public StreamAudioSender(OpusPacketHandler send, int bitrate = 128_000)
    {
        _send = send;
        _encoder = new StreamAudioEncoder(bitrate);
    }

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Silent frames that were not sent.</summary>
    public long FramesSkipped => Interlocked.Read(ref _framesSkipped);

    /// <summary>Adds captured samples (interleaved stereo; an odd trailing sample is kept for the next block).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            while (!interleaved.IsEmpty)
            {
                var count = Math.Min(interleaved.Length, _frame.Length - _filled);
                interleaved[..count].CopyTo(_frame.AsSpan(_filled));
                _filled += count;
                interleaved = interleaved[count..];
                if (_filled == _frame.Length)
                {
                    _filled = 0;
                    EncodeFrame();
                }
            }
        }
    }

    private void EncodeFrame()
    {
        var timestamp = _timestamp;
        _timestamp += DiscagemAudio.FrameSamples;

        var peak = 0f;
        foreach (var sample in _frame)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak < SilencePeak)
        {
            if (++_silentFrames > SilentFramesBeforePause)
            {
                Interlocked.Increment(ref _framesSkipped);
                return;
            }
        }
        else
        {
            _silentFrames = 0;
        }

        var length = _encoder.Encode(_frame, _packet);
        if (length > 0)
        {
            _send(_packet.AsSpan(0, length), timestamp);
            Interlocked.Increment(ref _packetsSent);
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

            _disposed = true;
            _encoder.Dispose();
        }
    }
}

/// <summary>
/// Plays the audio of a watched screen share: a jitter buffer and Opus decoder whose 20 ms frames
/// are handed out in blocks of whatever size the playback device asks for (48 kHz interleaved stereo).
/// </summary>
public sealed class StreamAudioReceiver : IDisposable
{
    private readonly SpeakerStream _stream;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly float[] _frame;
    private int _frameOffset;
    private int _frameRemaining;
    private float _volume = 1f;
    private bool _muted;
    private int _started;

    public StreamAudioReceiver(uint streamerId)
    {
        _stream = new SpeakerStream(streamerId, StreamAudioEncoder.Channels);
        _frame = new float[_stream.FrameLength];
    }

    /// <summary>Raised once, on the network thread, when the first audio packet arrives.</summary>
    public event Action? Started;

    public bool HasStarted => Volatile.Read(ref _started) != 0;

    public long PacketsReceived => _stream.PacketsReceived;

    public long FramesDecoded => _stream.FramesDecoded;

    public long FramesConcealed => _stream.FramesConcealed;

    public long FramesSkipped => _stream.FramesSkipped;

    /// <summary>Playback volume (1 = 100 %).</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            ApplyGain();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyGain();
        }
    }

    private void ApplyGain() => _stream.Gain = _muted ? 0f : _volume;

    public void Push(ushort sequence, ReadOnlySpan<byte> opus)
    {
        _stream.Push(sequence, opus, _clock.ElapsedMilliseconds);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            Started?.Invoke();
        }
    }

    /// <summary>Fills <paramref name="output"/> with interleaved stereo samples (silence while nothing is buffered).</summary>
    public void Read(Span<float> output)
    {
        while (!output.IsEmpty)
        {
            if (_frameRemaining == 0)
            {
                if (!_stream.Read(_frame, _clock.ElapsedMilliseconds))
                {
                    output.Clear();
                    return;
                }

                _frameOffset = 0;
                _frameRemaining = _frame.Length;
            }

            var count = Math.Min(_frameRemaining, output.Length);
            _frame.AsSpan(_frameOffset, count).CopyTo(output);
            _frameOffset += count;
            _frameRemaining -= count;
            output = output[count..];
        }
    }

    public void Dispose() => _stream.Dispose();
}
