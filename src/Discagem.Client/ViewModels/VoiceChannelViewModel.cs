using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>A voice channel and the people connected to it.</summary>
public sealed partial class VoiceChannelViewModel : ObservableObject
{
    public VoiceChannelViewModel(ChannelDto channel)
    {
        Id = channel.Id;
        Participants.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasParticipants));
        Update(channel);
    }

    public long Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _topic = "";

    [ObservableProperty]
    private int _position;

    /// <summary>True when the local user is in this channel.</summary>
    [ObservableProperty]
    private bool _isConnected;

    public ObservableCollection<VoiceParticipantViewModel> Participants { get; } = [];

    public bool HasParticipants => Participants.Count > 0;

    public void Update(ChannelDto channel)
    {
        Name = channel.Name;
        Topic = channel.Topic;
        Position = channel.Position;
    }

    public VoiceParticipantViewModel? Find(long userId) => Participants.FirstOrDefault(p => p.Member.Id == userId);
}

/// <summary>Someone in a voice channel.</summary>
public sealed partial class VoiceParticipantViewModel(
    MemberViewModel member,
    VoiceChannelViewModel channel,
    double volumePercent,
    Action<long, double>? volumeChanged) : ObservableObject
{
    public MemberViewModel Member { get; } = member;

    public VoiceChannelViewModel Channel { get; } = channel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMuted))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMuted))]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _isSpeaking;

    /// <summary>Volume of this person for the local user, in percent (100 = unchanged).</summary>
    [ObservableProperty]
    private double _volumePercent = volumePercent;

    partial void OnVolumePercentChanged(double value) => volumeChanged?.Invoke(Member.Id, value);

    /// <summary>The muted icon; deafened people show the deafened icon instead.</summary>
    public bool ShowMuted => IsMuted && !IsDeafened;

    public bool CanAdjustVolume => !Member.IsMe;

    public void Apply(VoiceParticipantDto dto)
    {
        IsMuted = dto.IsMuted;
        IsDeafened = dto.IsDeafened;
    }
}
