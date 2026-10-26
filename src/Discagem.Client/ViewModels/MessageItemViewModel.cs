using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Discagem.Shared;
using Discagem.Shared.Text;

namespace Discagem.Client.ViewModels;

/// <summary>A chat message as displayed in the channel view.</summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    public MessageItemViewModel(MessageDto message, MemberViewModel author, MemberViewModel me, Func<AttachmentDto, AttachmentViewModel> attachmentFactory)
    {
        Id = message.Id;
        ChannelId = message.ChannelId;
        Author = author;
        CreatedAt = message.CreatedAt;
        IsMine = author.Id == me.Id;
        CanDelete = IsMine || me.IsAdmin;
        Attachments = message.Attachments.Select(attachmentFactory).ToList();
        _me = me;
        Apply(message);
    }

    private readonly MemberViewModel _me;

    public long Id { get; }

    public long ChannelId { get; }

    public MemberViewModel Author { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsMine { get; }

    public bool CanDelete { get; }

    public IReadOnlyList<AttachmentViewModel> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    private string _content = "";

    [ObservableProperty]
    private bool _isEdited;

    [ObservableProperty]
    private bool _mentionsMe;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContinuation))]
    private bool _isGroupStart = true;

    [ObservableProperty]
    private bool _showDaySeparator;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    public bool IsContinuation => !IsGroupStart;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    public string TimestampLabel => FormatTimestamp(CreatedAt);

    public string ShortTime => CreatedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public string FullTimestamp => CreatedAt.ToLocalTime().ToString("F", CultureInfo.CurrentCulture);

    public string DayLabel => CreatedAt.ToLocalTime().ToString("D", CultureInfo.CurrentCulture);

    public void Apply(MessageDto message)
    {
        Content = message.Content;
        IsEdited = message.EditedAt is not null;
        MentionsMe = !IsMine && MessageMarkup.Mentions(message.Content, _me.Username);
    }

    public static string FormatTimestamp(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var today = DateTime.Now.Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return $"Today at {time}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday at {time}";
        }

        return $"{local.ToString("d", CultureInfo.CurrentCulture)} {time}";
    }
}
