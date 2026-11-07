using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Client.Services;
using Discagem.Media.Audio;

namespace Discagem.Client.ViewModels;

/// <summary>
/// The streamer's mixer: the applications whose sound goes into the live stream, each with its own
/// volume, remembered per application.
/// </summary>
public sealed partial class StreamMixerViewModel(ClientSettings settings) : ObservableObject
{
    private ApplicationAudioMixer? _mixer;

    public ObservableCollection<StreamApplicationViewModel> Applications { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoApplications))]
    private bool _isAvailable;

    public bool HasNoApplications => IsAvailable && Applications.Count == 0;

    /// <summary>Volume of an application the first time it shows up in a stream.</summary>
    public static float InitialVolume(ClientSettings settings, string key) =>
        settings.StreamApplicationVolumes.TryGetValue(key, out var volume) ? volume
        : StreamApplicationDefaults.ReplaysOtherApplications(key) ? 0f
        : 1f;

    /// <summary>Shows the applications of a mixer (null when the stream has no sound). Call on the UI thread.</summary>
    public void Refresh(ApplicationAudioMixer? mixer)
    {
        _mixer = mixer;
        var current = mixer?.Applications ?? [];
        for (var i = Applications.Count - 1; i >= 0; i--)
        {
            if (current.All(a => a.Key != Applications[i].Key))
            {
                Applications.RemoveAt(i);
            }
        }

        foreach (var application in current)
        {
            var row = Applications.FirstOrDefault(a => a.Key == application.Key);
            if (row is null)
            {
                Applications.Add(new StreamApplicationViewModel(this, application));
            }
            else
            {
                row.IsPlaying = application.IsPlaying;
            }
        }

        IsAvailable = mixer is not null;
        OnPropertyChanged(nameof(HasNoApplications));
    }

    internal void SetVolume(string key, double percent)
    {
        var volume = (float)(percent / 100);
        _mixer?.SetVolume(key, volume);
        settings.StreamApplicationVolumes[key] = volume;
        settings.Save();
    }
}

/// <summary>One application in the streamer's mixer.</summary>
public sealed partial class StreamApplicationViewModel : ObservableObject
{
    private readonly StreamMixerViewModel _owner;
    private bool _initialised;

    internal StreamApplicationViewModel(StreamMixerViewModel owner, StreamApplication application)
    {
        _owner = owner;
        Key = application.Key;
        Name = application.Name;
        _volumePercent = Math.Round(application.Volume * 100);
        _isPlaying = application.IsPlaying;
        _initialised = true;
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>Volume in the stream, 0–100 %.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel), nameof(IsMuted))]
    private double _volumePercent;

    [ObservableProperty]
    private bool _isPlaying;

    public string VolumeLabel => VolumePercent <= 0 ? "Off" : $"{VolumePercent:0}%";

    public bool IsMuted => VolumePercent <= 0;

    partial void OnVolumePercentChanged(double value)
    {
        if (_initialised)
        {
            _owner.SetVolume(Key, value);
        }
    }
}

/// <summary>Defaults for applications in the sound of a stream.</summary>
internal static class StreamApplicationDefaults
{
    /// <summary>
    /// Parts of the executable names of programs that replay what other programs play, Discagem's voice
    /// chat included: headset software, virtual mixers and system-wide equalisers. They start muted
    /// in the mixer, or the people in a call would hear themselves through the stream.
    /// </summary>
    private static readonly string[] Replayers =
    [
        "sonar",        // SteelSeries Sonar
        "voicemeeter",  // VB-Audio Voicemeeter
        "wavelink",     // Elgato Wave Link
        "fxsound",
        "audiorelay",
        "easyeffects",  // Linux (PipeWire)
        "pulseeffects", // Linux (PulseAudio)
    ];

    public static bool ReplaysOtherApplications(string key) =>
        Replayers.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
}
