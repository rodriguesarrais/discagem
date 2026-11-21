using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Media.Audio;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Discagem.Client.ViewModels;

/// <summary>
/// The soundboard of the voice card: the server's sounds, which anyone in a voice channel can play
/// for everybody in it. Playing only sends the sound's id; every client downloads each sound once and
/// plays it itself.
/// </summary>
public sealed partial class SoundboardViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly ChatConnection _hub;
    private readonly VoiceService _voice;
    private readonly ClientSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Func<MemberViewModel> _me;
    private readonly Action<string> _showError;
    private readonly ConcurrentDictionary<long, Task<float[]?>> _samples = new();

    public SoundboardViewModel(
        ApiClient api,
        ChatConnection hub,
        VoiceService voice,
        ClientSettings settings,
        IDialogService dialogs,
        Func<MemberViewModel> me,
        Action<string> showError)
    {
        _api = api;
        _hub = hub;
        _voice = voice;
        _settings = settings;
        _dialogs = dialogs;
        _me = me;
        _showError = showError;
        _volumePercent = Math.Round(settings.SoundboardVolume * 100);
        Sounds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSounds));
    }

    public ObservableCollection<SoundItemViewModel> Sounds { get; } = [];

    public bool HasNoSounds => Sounds.Count == 0;

    /// <summary>False for servers that predate the soundboard.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private bool _canPlay;

    /// <summary>Why sounds cannot be played right now.</summary>
    public string StatusText => CanPlay ? "" : "Unmute yourself to play sounds.";

    public bool HasStatus => !CanPlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private double _volumePercent;

    public string VolumeText => $"{VolumePercent:0} %";

    partial void OnVolumePercentChanged(double value)
    {
        _settings.SoundboardVolume = (float)Math.Clamp(value / 100, 0, 1);
        _settings.Save();
    }

    /// <summary>Updates what the voice card allows: sounds play only in a call, unmuted.</summary>
    public void UpdateState(bool inVoice, bool microphoneOff) => CanPlay = inVoice && !microphoneOff;

    public void Reset(IReadOnlyList<SoundDto>? sounds)
    {
        IsAvailable = sounds is not null;
        Sounds.Clear();
        foreach (var sound in sounds ?? [])
        {
            Add(sound);
        }
    }

    public void Add(SoundDto sound)
    {
        if (Sounds.All(s => s.Id != sound.Id))
        {
            Sounds.Add(new SoundItemViewModel(sound, sound.UploaderId == _me().Id || _me().IsAdmin));
        }
    }

    public void Remove(long id)
    {
        _samples.TryRemove(id, out _);
        if (Sounds.FirstOrDefault(s => s.Id == id) is { } item)
        {
            Sounds.Remove(item);
        }
    }

    /// <summary>Someone in the call played a sound (the server only sends these to the people in the channel).</summary>
    public async void OnSoundPlayed(long channelId, long userId, long soundId)
    {
        if (_voice.ChannelId != channelId)
        {
            return;
        }

        var samples = await GetSamplesAsync(soundId);
        if (samples is not null && _voice.ChannelId == channelId)
        {
            _voice.PlaySound(samples);
        }
    }

    /// <summary>The soundboard opened: fetch the sounds now, so that they play at once.</summary>
    public void OnOpened()
    {
        foreach (var sound in Sounds)
        {
            _ = GetSamplesAsync(sound.Id);
        }
    }

    private Task<float[]?> GetSamplesAsync(long id) => _samples.GetOrAdd(id, LoadAsync);

    private async Task<float[]?> LoadAsync(long id)
    {
        try
        {
            var wav = await _api.DownloadSoundAsync(id);
            return await Task.Run(() => WavAudio.ReadMono(wav));
        }
        catch (Exception ex) when (ex is ApiException or InvalidDataException)
        {
            _samples.TryRemove(id, out _);
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync(SoundItemViewModel? sound)
    {
        if (sound is null)
        {
            return;
        }

        _ = GetSamplesAsync(sound.Id);
        try
        {
            await _hub.PlaySoundAsync(sound.Id);
        }
        catch (Exception ex) when (ex is HubException or InvalidOperationException)
        {
            _showError(HubErrors.Describe(ex));
        }
    }

    /// <summary>Plays a sound for the local user only.</summary>
    [RelayCommand]
    private async Task PreviewAsync(SoundItemViewModel? sound)
    {
        if (sound is null)
        {
            return;
        }

        if (await GetSamplesAsync(sound.Id) is { } samples)
        {
            _voice.PlaySound(samples);
        }
        else
        {
            _showError($"Could not load the sound \"{sound.Name}\".");
        }
    }

    [RelayCommand]
    private void Stop() => _voice.StopSounds();

    [RelayCommand]
    private async Task AddAsync()
    {
        var file = await _dialogs.PickSoundAsync();
        if (file is null)
        {
            return;
        }

        float[] clip;
        bool trimmed;
        try
        {
            using var buffer = new MemoryStream();
            await using (var stream = await file.OpenReadAsync())
            {
                await stream.CopyToAsync(buffer);
            }

            buffer.Position = 0;
            (clip, trimmed) = await Task.Run(() =>
            {
                var decoded = SoundClips.Decode(buffer, file.Name);
                var prepared = SoundClips.Prepare(decoded, Limits.MaxSoundMilliseconds);
                return (prepared, decoded.Length > prepared.Length + Discagem.Media.WebRtc.DiscagemAudio.SampleRate / 2);
            });
        }
        catch (InvalidDataException ex)
        {
            _showError(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showError($"Could not read {file.Name}.");
            return;
        }

        if (clip.Length == 0)
        {
            _showError($"{file.Name} is silent.");
            return;
        }

        var seconds = clip.Length / (double)Discagem.Media.WebRtc.DiscagemAudio.SampleRate;
        var details = trimmed
            ? $"{seconds:0.0} seconds: sounds can be at most {Limits.MaxSoundMilliseconds / 1000} seconds long, so only the start is kept."
            : $"{seconds:0.0} seconds. Everyone in your voice channel hears it when you play it.";
        var suggestion = Path.GetFileNameWithoutExtension(file.Name).Replace('_', ' ').Trim();
        if (suggestion.Length > Limits.MaxSoundNameLength)
        {
            suggestion = suggestion[..Limits.MaxSoundNameLength].Trim();
        }

        var dialog = UploadExpressionViewModel.ForSound(suggestion, details, () => _voice.PlaySound(clip));
        if (!await _dialogs.ShowUploadExpressionAsync(dialog))
        {
            return;
        }

        try
        {
            var sound = await _api.UploadSoundAsync(dialog.Name.Trim(), SoundClips.EncodeWav(clip));
            _samples[sound.Id] = Task.FromResult<float[]?>(clip);
            Add(sound);
        }
        catch (ApiException ex)
        {
            _showError($"Could not add the sound. {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SoundItemViewModel? sound)
    {
        if (sound is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove sound", $"Remove \"{sound.Name}\" from the soundboard for everyone?", "Remove", danger: true))
        {
            return;
        }

        try
        {
            await _api.DeleteSoundAsync(sound.Id);
            Remove(sound.Id);
        }
        catch (ApiException ex)
        {
            _showError($"Could not remove the sound. {ex.Message}");
        }
    }
}

/// <summary>A sound on the soundboard.</summary>
public sealed class SoundItemViewModel(SoundDto sound, bool canDelete)
{
    public SoundDto Sound { get; } = sound;

    public long Id => Sound.Id;

    public string Name => Sound.Name;

    public string DurationText => $"{Sound.DurationMs / 1000.0:0.0} s";

    public bool CanDelete { get; } = canDelete;
}
