using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Discagem.Client.Services;
using Discagem.Shared;

namespace Discagem.Client.ViewModels;

/// <summary>Colours offered for rooms (and, as the room's initials background, before it has an icon).</summary>
public static class RoomColors
{
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#5865F2", "#3BA55C", "#FAA61A", "#ED4245", "#EB459E", "#9B59B6",
        "#1ABC9C", "#E67E22", "#3498DB", "#607D8B", "#2C3E50", "#F1C40F",
    ];
}

/// <summary>The text people send to invite someone to a room.</summary>
public static class RoomInvite
{
    public static string Text(string roomName, string serverAddress, string code) =>
        $"Join {roomName} on Discagem: open Discagem, sign in to {serverAddress}, choose \"Join a room\" and enter the code {code}";
}

/// <summary>Creating a room, or joining one with an invite code.</summary>
public sealed partial class RoomEntryViewModel : ObservableObject
{
    public RoomEntryViewModel(bool join)
    {
        _isJoin = join;
        _color = RoomColors.Palette[Random.Shared.Next(RoomColors.Palette.Count)];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreate), nameof(ConfirmText), nameof(CanConfirm))]
    private bool _isJoin;

    public bool IsCreate
    {
        get => !IsJoin;
        set => IsJoin = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(Initials))]
    private string _name = "";

    [ObservableProperty]
    private string _color;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _inviteCode = "";

    public IReadOnlyList<string> Palette => RoomColors.Palette;

    public string Initials => Controls.AvatarView.Initials(string.IsNullOrWhiteSpace(Name) ? "?" : Name);

    public string ConfirmText => IsJoin ? "Join room" : "Create room";

    public bool CanConfirm => IsJoin ? !string.IsNullOrWhiteSpace(InviteCode) : !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private void PickColor(string color) => Color = color;
}

/// <summary>
/// A room's settings: its name, colour and icon, its invite code and its members. People who do not
/// manage the room see the invite and the members only.
/// </summary>
public sealed partial class RoomSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ChatConnection _hub;
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyDictionary<long, MemberViewModel> _members;
    private readonly MemberViewModel _me;
    private readonly Action<string> _showError;

    public RoomSettingsViewModel(
        RoomViewModel room,
        ChatConnection hub,
        ApiClient api,
        IDialogService dialogs,
        IReadOnlyDictionary<long, MemberViewModel> members,
        MemberViewModel me,
        Action<string> showError)
    {
        Room = room;
        _hub = hub;
        _api = api;
        _dialogs = dialogs;
        _members = members;
        _me = me;
        _showError = showError;
        _name = room.Name;
        _color = room.Color;
        ServerAddress = api.BaseAddress.ToString().TrimEnd('/');
        Room.PropertyChanged += OnRoomChanged;
        RefreshMembers();
    }

    public RoomViewModel Room { get; }

    public string ServerAddress { get; }

    public bool CanManage => Room.IsOwner(_me.Id) || _me.IsAdmin;

    public bool IsOwner => Room.IsOwner(_me.Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private string _color;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasChanges => Name.Trim() != Room.Name || Color != Room.Color;

    public IReadOnlyList<string> Palette => RoomColors.Palette;

    public ObservableCollection<RoomMemberViewModel> Members { get; } = [];

    public string MembersHeader => $"MEMBERS — {Members.Count}";

    /// <summary>Raised when the room should close (it was deleted, or the user left it).</summary>
    public event Action? CloseRequested;

    private void OnRoomChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoomViewModel.MemberIds) or nameof(RoomViewModel.OwnerId))
        {
            RefreshMembers();
            OnPropertyChanged(nameof(CanManage));
            OnPropertyChanged(nameof(IsOwner));
        }
    }

    private void RefreshMembers()
    {
        Members.Clear();
        var canManage = CanManage;
        foreach (var id in Room.MemberIds)
        {
            if (_members.TryGetValue(id, out var member))
            {
                Members.Add(new RoomMemberViewModel(member, Room.IsOwner(id), canManage && !Room.IsOwner(id) && id != _me.Id));
            }
        }

        var ordered = Members.OrderByDescending(m => m.IsOwner).ThenBy(m => m.Member.SortKey).ToList();
        Members.Clear();
        foreach (var member in ordered)
        {
            Members.Add(member);
        }

        OnPropertyChanged(nameof(MembersHeader));
    }

    [RelayCommand]
    private void PickColor(string color) => Color = color;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunAsync(() => _hub.UpdateRoomAsync(Room.Id, Name.Trim(), Color), "Could not save the room");
        OnPropertyChanged(nameof(HasChanges));
    }

    [RelayCommand]
    private async Task ChangeIconAsync()
    {
        var file = await _dialogs.PickImageAsync("Choose an icon for the room");
        if (file is null)
        {
            return;
        }

        byte[] png;
        try
        {
            await using var stream = await file.OpenReadAsync();
            png = AvatarImages.Prepare(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            _showError("That file is not a picture Discagem can read.");
            return;
        }

        await RunAsync(async () => Room.Update(await _api.UploadRoomIconAsync(Room.Id, png)), "Could not change the icon");
    }

    [RelayCommand]
    private Task RemoveIconAsync() =>
        RunAsync(async () => Room.Update(await _api.RemoveRoomIconAsync(Room.Id)), "Could not remove the icon");

    [RelayCommand]
    private async Task CopyInviteAsync() =>
        await _dialogs.CopyTextAsync(RoomInvite.Text(Room.Name, ServerAddress, Room.InviteCode));

    [RelayCommand]
    private async Task ResetInviteAsync()
    {
        if (await _dialogs.ConfirmAsync("Reset invite code", "The current code stops working. People already in the room stay.", "Reset code", danger: false))
        {
            await RunAsync(() => _hub.ResetRoomInviteAsync(Room.Id), "Could not reset the invite code");
        }
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(RoomMemberViewModel member)
    {
        if (await _dialogs.ConfirmAsync("Remove from room", $"Remove {member.Member.DisplayName} from {Room.Name}? They can come back with an invite.", "Remove", danger: true))
        {
            await RunAsync(() => _hub.RemoveRoomMemberAsync(Room.Id, member.Member.Id), "Could not remove the member");
        }
    }

    [RelayCommand]
    private async Task TransferAsync(RoomMemberViewModel member)
    {
        if (await _dialogs.ConfirmAsync("Transfer room", $"Make {member.Member.DisplayName} the owner of {Room.Name}? They will manage it from now on.", "Transfer", danger: true))
        {
            await RunAsync(() => _hub.TransferRoomAsync(Room.Id, member.Member.Id), "Could not transfer the room");
        }
    }

    [RelayCommand]
    private async Task DeleteRoomAsync()
    {
        if (!await _dialogs.ConfirmAsync("Delete room", $"Delete {Room.Name} for everyone, with all of its channels and messages? This cannot be undone.", "Delete room", danger: true))
        {
            return;
        }

        await RunAsync(() => _hub.DeleteRoomAsync(Room.Id), "Could not delete the room");
        CloseRequested?.Invoke();
    }

    private async Task RunAsync(Func<Task> action, string context)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _showError($"{context}: {HubErrors.Describe(ex)}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose() => Room.PropertyChanged -= OnRoomChanged;
}

/// <summary>A member in the room's settings.</summary>
public sealed class RoomMemberViewModel(MemberViewModel member, bool isOwner, bool canManage)
{
    public MemberViewModel Member { get; } = member;

    public bool IsOwner { get; } = isOwner;

    /// <summary>Whether the viewer can remove this member or hand them the room.</summary>
    public bool CanManage { get; } = canManage;
}
