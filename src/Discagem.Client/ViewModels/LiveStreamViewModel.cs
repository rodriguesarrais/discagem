using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>An entry in the "Live now" list.</summary>
public sealed partial class LiveStreamViewModel : ObservableObject
{
    public LiveStreamViewModel(StreamInfoDto stream, MemberViewModel streamer)
    {
        StreamId = stream.StreamId;
        Streamer = streamer;
        Update(stream);
    }

    public string StreamId { get; }

    public MemberViewModel Streamer { get; }

    public bool IsMine => Streamer.IsMe;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewers))]
    private int _viewerCount;

    [ObservableProperty]
    private string _formatLabel = "";

    /// <summary>Whether the streamer shares their computer's sound.</summary>
    [ObservableProperty]
    private bool _hasAudio;

    public bool HasViewers => ViewerCount > 0;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"{Streamer.DisplayName}'s screen" : Title;

    public void Update(StreamInfoDto stream)
    {
        Title = stream.Title;
        ViewerCount = stream.ViewerIds.Count;
        FormatLabel = $"{stream.Height}p · {stream.FrameRate} FPS";
        HasAudio = stream.HasAudio;
        OnPropertyChanged(nameof(DisplayTitle));
    }
}
