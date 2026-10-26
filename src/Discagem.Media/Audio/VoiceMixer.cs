using System.Diagnostics;
using Discagem.Media.WebRtc;

namespace Discagem.Media.Audio;

/// <summary>
/// Mixes every remote speaker (and notification cues) into a single 48 kHz mono signal, pulled by
/// the playback device in blocks of any size.
/// </summary>
/// <remarks>
/// <see cref="Push"/> is called from the network thread, <see cref="Read"/> from the audio thread
/// (one reader at a time); everything else may be called from any thread.
/// </remarks>
public sealed class VoiceMixer : IDisposable
{
    private const int FrameSamples = DiscagemAudio.FrameSamples;
    private const int MaxSpeakers = 64;

    private readonly object _sync = new();
    private readonly Func<long> _clock;
    private readonly Dictionary<uint, SpeakerStream> _speakers = new();
    private readonly Dictionary<uint, float> _gains = new();
    private readonly HashSet<uint> _speaking = new();
    private readonly List<Cue> _cues = new();
    private readonly float[] _mix = new float[FrameSamples];
    private readonly float[] _frame = new float[FrameSamples];
    private SpeakerStream[] _snapshot = [];
    private bool _snapshotStale = true;
    private int _position = FrameSamples;

    public VoiceMixer(Func<long>? clock = null)
    {
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            clock = () => stopwatch.ElapsedMilliseconds;
        }

        _clock = clock;
    }

    /// <summary>Master playback volume (1 = unchanged).</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Silences every speaker (deafened) while still playing notification cues.</summary>
    public bool MuteVoices { get; set; }

    /// <summary>Raised on the audio thread when a speaker starts or stops being audible.</summary>
    public event Action<uint, bool>? SpeakingChanged;

    public IReadOnlyList<SpeakerStream> Speakers
    {
        get
        {
            lock (_sync)
            {
                return _speakers.Values.ToList();
            }
        }
    }

    public void Push(uint speakerId, ushort sequence, ReadOnlySpan<byte> opus)
    {
        SpeakerStream? stream;
        lock (_sync)
        {
            if (!_speakers.TryGetValue(speakerId, out stream))
            {
                if (_speakers.Count >= MaxSpeakers)
                {
                    return;
                }

                stream = new SpeakerStream(speakerId) { Gain = _gains.GetValueOrDefault(speakerId, 1f) };
                _speakers[speakerId] = stream;
                _snapshotStale = true;
            }
        }

        stream.Push(sequence, opus, _clock());
    }

    /// <summary>Forgets a speaker (they left the channel). Their volume setting is kept.</summary>
    public void RemoveSpeaker(uint speakerId)
    {
        SpeakerStream? stream;
        bool wasSpeaking;
        lock (_sync)
        {
            if (!_speakers.Remove(speakerId, out stream))
            {
                return;
            }

            _snapshotStale = true;
            wasSpeaking = _speaking.Remove(speakerId);
        }

        stream.Dispose();
        if (wasSpeaking)
        {
            SpeakingChanged?.Invoke(speakerId, false);
        }
    }

    public void SetSpeakerGain(uint speakerId, float gain)
    {
        gain = Math.Clamp(gain, 0f, 4f);
        lock (_sync)
        {
            _gains[speakerId] = gain;
            if (_speakers.TryGetValue(speakerId, out var stream))
            {
                stream.Gain = gain;
            }
        }
    }

    public float GetSpeakerGain(uint speakerId)
    {
        lock (_sync)
        {
            return _gains.GetValueOrDefault(speakerId, 1f);
        }
    }

    public bool IsSpeaking(uint speakerId)
    {
        lock (_sync)
        {
            return _speaking.Contains(speakerId);
        }
    }

    public void PlayCue(float[] samples)
    {
        lock (_sync)
        {
            if (_cues.Count < 8)
            {
                _cues.Add(new Cue(samples));
            }
        }
    }

    /// <summary>Fills <paramref name="output"/> with the next samples of the mix.</summary>
    public void Read(Span<float> output)
    {
        while (!output.IsEmpty)
        {
            if (_position >= FrameSamples)
            {
                MixFrame();
                _position = 0;
            }

            var count = Math.Min(FrameSamples - _position, output.Length);
            _mix.AsSpan(_position, count).CopyTo(output);
            _position += count;
            output = output[count..];
        }
    }

    private void MixFrame()
    {
        Array.Clear(_mix);
        SpeakerStream[] streams;
        lock (_sync)
        {
            if (_snapshotStale)
            {
                _snapshot = _speakers.Values.ToArray();
                _snapshotStale = false;
            }

            streams = _snapshot;
        }

        var now = _clock();
        var muteVoices = MuteVoices;
        List<(uint Speaker, bool Speaking)>? changes = null;
        foreach (var stream in streams)
        {
            // Always pull, even when deafened, so that buffers drain at the normal pace.
            if (stream.Read(_frame, now) && !muteVoices)
            {
                for (var i = 0; i < FrameSamples; i++)
                {
                    _mix[i] += _frame[i];
                }
            }

            var speaking = stream.IsSpeaking(now) && !muteVoices;
            lock (_sync)
            {
                var changed = speaking ? _speaking.Add(stream.SpeakerId) : _speaking.Remove(stream.SpeakerId);
                if (changed && _speakers.ContainsKey(stream.SpeakerId))
                {
                    (changes ??= []).Add((stream.SpeakerId, speaking));
                }
            }
        }

        var volume = Volume;
        for (var i = 0; i < FrameSamples; i++)
        {
            _mix[i] *= volume;
        }

        lock (_sync)
        {
            for (var c = _cues.Count - 1; c >= 0; c--)
            {
                var cue = _cues[c];
                var count = Math.Min(FrameSamples, cue.Samples.Length - cue.Position);
                for (var i = 0; i < count; i++)
                {
                    _mix[i] += cue.Samples[cue.Position + i];
                }

                cue.Position += count;
                if (cue.Position >= cue.Samples.Length)
                {
                    _cues.RemoveAt(c);
                }
            }
        }

        for (var i = 0; i < FrameSamples; i++)
        {
            _mix[i] = Math.Clamp(_mix[i], -1f, 1f);
        }

        if (changes is not null)
        {
            foreach (var (speaker, speaking) in changes)
            {
                SpeakingChanged?.Invoke(speaker, speaking);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var stream in _speakers.Values)
            {
                stream.Dispose();
            }

            _speakers.Clear();
            _snapshot = [];
            _speaking.Clear();
        }
    }

    private sealed class Cue(float[] samples)
    {
        public float[] Samples { get; } = samples;

        public int Position { get; set; }
    }
}
