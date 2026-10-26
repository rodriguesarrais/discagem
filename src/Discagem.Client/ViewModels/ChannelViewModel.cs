using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>A text channel together with its loaded message history.</summary>
public sealed partial class ChannelViewModel : ObservableObject
{
    private static readonly TimeSpan GroupingWindow = TimeSpan.FromMinutes(7);

    public ChannelViewModel(ChannelDto channel)
    {
        Id = channel.Id;
        Update(channel);
    }

    public long Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopic))]
    private string _topic = "";

    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMentions))]
    private int _mentionCount;

    [ObservableProperty]
    private bool _hasMoreHistory = true;

    [ObservableProperty]
    private bool _isLoadingHistory;

    public bool IsLoaded { get; set; }

    public bool HasTopic => !string.IsNullOrWhiteSpace(Topic);

    public bool HasUnread => UnreadCount > 0;

    public bool HasMentions => MentionCount > 0;

    public string Placeholder => $"Message #{Name}";

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    public void Update(ChannelDto channel)
    {
        Name = channel.Name;
        Topic = channel.Topic;
        Position = channel.Position;
        OnPropertyChanged(nameof(Placeholder));
    }

    public void MarkRead()
    {
        UnreadCount = 0;
        MentionCount = 0;
    }

    /// <summary>Recomputes which messages start a new group and where day separators go.</summary>
    public void RecomputeGrouping()
    {
        MessageItemViewModel? previous = null;
        foreach (var message in Messages)
        {
            var local = message.CreatedAt.ToLocalTime();
            var newDay = previous is null || previous.CreatedAt.ToLocalTime().Date != local.Date;
            message.ShowDaySeparator = newDay;
            message.IsGroupStart = newDay ||
                                   previous!.Author.Id != message.Author.Id ||
                                   message.CreatedAt - previous.CreatedAt > GroupingWindow;
            previous = message;
        }
    }

    /// <summary>Inserts a message keeping the list ordered by id. Returns false for duplicates.</summary>
    public bool Insert(MessageItemViewModel message)
    {
        var index = Messages.Count;
        while (index > 0 && Messages[index - 1].Id > message.Id)
        {
            index--;
        }

        if (index > 0 && Messages[index - 1].Id == message.Id)
        {
            return false;
        }

        Messages.Insert(index, message);
        return true;
    }

    public MessageItemViewModel? Find(long messageId) => Messages.FirstOrDefault(m => m.Id == messageId);
}
