using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>Receives an encoded Opus packet and its RTP timestamp.</summary>
public delegate void OpusPacketHandler(ReadOnlySpan<byte> packet, uint timestamp);

/// <summary>
/// Platform-independent voice processing for one call: microphone samples go in through
/// <see cref="WriteCapture"/> and come out as Opus packets (only while speaking, unless muted);
/// remote packets go into the <see cref="Mixer"/> and come out of <see cref="ReadPlayback"/>.
/// Audio devices exchange 48 kHz mono float samples with it.
/// </summary>
public sealed class VoiceEngine : IDisposable
{
    private const int FrameSamples = DiscagemAudio.FrameSamples;

    private readonly object _captureSync = new();
    private readonly OpusVoiceEncoder _encoder = new();
    private readonly VoiceActivityDetector _vad = new();
    private readonly HighPassFilter _highPass = new();
    private readonly float[] _frame = new float[FrameSamples];
    private readonly byte[] _packet = new byte[OpusVoiceEncoder.MaxPacketBytes];
    private readonly byte[] _preRoll = new byte[OpusVoiceEncoder.MaxPacketBytes];
    private int _frameFill;
    private int _preRollLength;
    private uint _preRollTimestamp;
    private uint _timestamp = (uint)Random.Shared.Next();
    private volatile bool _transmitting;
    private volatile bool _muted;
    private volatile float _inputGain = 1f;
    private long _packetsEncoded;
    private int _disposed;

    public VoiceEngine(Func<long>? clock = null)
    {
        Mixer = new VoiceMixer(clock);
    }

    public VoiceMixer Mixer { get; }

    /// <summary>Where encoded packets go (called on the capture thread).</summary>
    public OpusPacketHandler? PacketReady { get; set; }

    /// <summary>Microphone off: nothing is encoded or sent, but the input level is still measured.</summary>
    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Speakers off (and, as in Discord, the microphone too). Cues still play.</summary>
    public bool IsDeafened
    {
        get => Mixer.MuteVoices;
        set => Mixer.MuteVoices = value;
    }

    /// <summary>Input sensitivity: the level (dBFS) above which the microphone transmits.</summary>
    public float InputThresholdDb
    {
        get => _vad.ThresholdDb;
        set => _vad.ThresholdDb = Math.Clamp(value, -100f, 0f);
    }

    public float InputGain
    {
        get => _inputGain;
        set => _inputGain = Math.Clamp(value, 0f, 8f);
    }

    public float OutputVolume
    {
        get => Mixer.Volume;
        set => Mixer.Volume = Math.Clamp(value, 0f, 4f);
    }

    /// <summary>Level of the last microphone frame, in dBFS (for meters).</summary>
    public float InputLevelDb => _vad.LevelDb;

    /// <summary>True while the microphone is being transmitted.</summary>
    public bool IsTransmitting => _transmitting;

    public long PacketsEncoded => Interlocked.Read(ref _packetsEncoded);

    /// <summary>Raised on the capture thread when transmission starts or stops.</summary>
    public event Action<bool>? TransmittingChanged;

    /// <summary>Feeds microphone samples (48 kHz mono, any block size).</summary>
    public void WriteCapture(ReadOnlySpan<float> samples)
    {
        lock (_captureSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            while (!samples.IsEmpty)
            {
                var count = Math.Min(FrameSamples - _frameFill, samples.Length);
                samples[..count].CopyTo(_frame.AsSpan(_frameFill));
                _frameFill += count;
                samples = samples[count..];
                if (_frameFill == FrameSamples)
                {
                    ProcessFrame();
                    _frameFill = 0;
                }
            }
        }
    }

    private void ProcessFrame()
    {
        var frame = _frame.AsSpan();
        _highPass.Process(frame);
        var gain = _inputGain;
        if (gain != 1f)
        {
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = Math.Clamp(frame[i] * gain, -1f, 1f);
            }
        }

        var speech = _vad.Process(frame);
        var timestamp = _timestamp;
        _timestamp += FrameSamples;

        var send = speech && !_muted && !IsDeafened;
        if (_muted || IsDeafened)
        {
            _preRollLength = 0;
        }
        else
        {
            // Encode continuously so that the encoder state stays smooth across the gate.
            var length = _encoder.Encode(frame, _packet);
            Interlocked.Increment(ref _packetsEncoded);
            if (send)
            {
                // Send the frame just before speech started too, so word onsets are not clipped.
                if (!_transmitting && _preRollLength > 0)
                {
                    PacketReady?.Invoke(_preRoll.AsSpan(0, _preRollLength), _preRollTimestamp);
                }

                PacketReady?.Invoke(_packet.AsSpan(0, length), timestamp);
                _preRollLength = 0;
            }
            else
            {
                _packet.AsSpan(0, length).CopyTo(_preRoll);
                _preRollLength = length;
                _preRollTimestamp = timestamp;
            }
        }

        if (send != _transmitting)
        {
            _transmitting = send;
            TransmittingChanged?.Invoke(send);
        }
    }

    /// <summary>Fills a playback buffer (48 kHz mono, any block size).</summary>
    public void ReadPlayback(Span<float> output)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            output.Clear();
            return;
        }

        Mixer.Read(output);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_captureSync)
        {
            _encoder.Dispose();
        }

        Mixer.Dispose();
    }
}
