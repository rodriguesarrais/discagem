namespace Discagem.Server.Hubs;

/// <summary>SignalR groups: every connection of a room's members is in the room's group.</summary>
public static class RoomGroups
{
    public static string Name(long roomId) => $"room:{roomId}";
}
