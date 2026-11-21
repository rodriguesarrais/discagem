using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Hubs;
using Discagem.Server.Options;
using Discagem.Server.Services;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

/// <summary>
/// Room icons. Clients crop and scale pictures before uploading them; the server only accepts small
/// PNG or JPEG files and serves them to the room's members. Everything else about rooms goes through
/// the hub.
/// </summary>
public static class RoomEndpoints
{
    public static void MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rooms/{id:long}/icon").RequireAuthorization();
        group.MapPut("/", Upload).DisableAntiforgery();
        group.MapDelete("/", Remove);
        group.MapGet("/", Download);
    }

    private static async Task<IResult> Upload(
        long id,
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub,
        CancellationToken cancellationToken)
    {
        if (await ManagedRoomAsync(id, context, db) is not { } room)
        {
            return Results.NotFound(new ApiError("That room does not exist."));
        }

        if (!CanManage(room, context))
        {
            return Results.Json(new ApiError("Only the room's owner can change its icon."), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new ApiError("Expected a multipart/form-data request."));
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ApiError("No picture was uploaded."));
        }

        if (file.Length > Limits.MaxRoomIconBytes)
        {
            return Results.Json(new ApiError($"Room icons may be at most {Limits.MaxRoomIconBytes / 1024} KB."), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var data = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(data, cancellationToken);
        }

        if (AvatarEndpoints.ContentType(data) is null)
        {
            return Results.BadRequest(new ApiError("Room icons must be PNG or JPEG images."));
        }

        if (!await StoredFiles.TryWriteAsync(ChatHub.RoomIconPath(options.Value, room.Id), data, cancellationToken))
        {
            return Results.Problem("The icon could not be saved.");
        }

        room.HasIcon = true;
        room.IconVersion++;
        await db.SaveChangesAsync(cancellationToken);
        var dto = room.ToDto();
        await hub.Clients.Group(RoomGroups.Name(room.Id)).RoomUpdated(dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> Remove(
        long id,
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub)
    {
        if (await ManagedRoomAsync(id, context, db) is not { } room)
        {
            return Results.NotFound(new ApiError("That room does not exist."));
        }

        if (!CanManage(room, context))
        {
            return Results.Json(new ApiError("Only the room's owner can change its icon."), statusCode: StatusCodes.Status403Forbidden);
        }

        AttachmentCleanupService.TryDelete(ChatHub.RoomIconPath(options.Value, room.Id));
        if (room.HasIcon)
        {
            room.HasIcon = false;
            room.IconVersion++;
            await db.SaveChangesAsync();
            await hub.Clients.Group(RoomGroups.Name(room.Id)).RoomUpdated(room.ToDto());
        }

        return Results.Ok(room.ToDto());
    }

    private static async Task<IResult> Download(long id, HttpContext context, AppDbContext db, IOptions<DiscagemOptions> options)
    {
        var userId = context.User.GetUserId();
        var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.Members.Any(m => m.UserId == userId));
        var path = ChatHub.RoomIconPath(options.Value, id);
        if (room is null || !room.HasIcon || !File.Exists(path))
        {
            return Results.NotFound();
        }

        var data = await File.ReadAllBytesAsync(path);
        var contentType = AvatarEndpoints.ContentType(data);
        if (contentType is null)
        {
            return Results.NotFound();
        }

        // Clients ask for ?v=<IconVersion>, so a new icon is a new URL.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Bytes(data, contentType);
    }

    /// <summary>The room, with its members, if the caller is one of them.</summary>
    private static async Task<Room?> ManagedRoomAsync(long id, HttpContext context, AppDbContext db)
    {
        var userId = context.User.GetUserId();
        var room = await db.Rooms.Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == id);
        return room is not null && room.Members.Any(m => m.UserId == userId) ? room : null;
    }

    private static bool CanManage(Room room, HttpContext context) =>
        room.OwnerId == context.User.GetUserId() || context.User.IsAdmin();
}
