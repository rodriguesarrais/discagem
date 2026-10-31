using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Hubs;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

/// <summary>
/// Profile photos. Clients crop and scale photos before uploading them; the server only accepts
/// small PNG or JPEG files, stores one per user and serves it to signed-in users.
/// </summary>
public static class AvatarEndpoints
{
    public const int MaxAvatarBytes = 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    public static void MapAvatarEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me/avatar").RequireAuthorization();
        me.MapPut("/", Upload).DisableAntiforgery();
        me.MapDelete("/", Remove);

        app.MapGet("/api/users/{id:long}/avatar", Download).RequireAuthorization();
    }

    private static async Task<IResult> Upload(
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FindAsync([context.User.GetUserId()], cancellationToken);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new ApiError("Expected a multipart/form-data request."));
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ApiError("No photo was uploaded."));
        }

        if (file.Length > MaxAvatarBytes)
        {
            return Results.Json(new ApiError("Profile photos may be at most 1 MB."), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var data = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(data, cancellationToken);
        }

        if (ContentType(data) is null)
        {
            return Results.BadRequest(new ApiError("Profile photos must be PNG or JPEG images."));
        }

        // Write to a temporary file first so that a failed upload never leaves half a photo behind.
        var path = AvatarPath(options.Value, user.Id);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, cancellationToken);
        File.Move(temporary, path, overwrite: true);

        user.HasAvatar = true;
        user.AvatarVersion++;
        await db.SaveChangesAsync(cancellationToken);
        var dto = user.ToDto();
        await hub.Clients.All.UserUpdated(dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> Remove(
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub)
    {
        var user = await db.Users.FindAsync(context.User.GetUserId());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        File.Delete(AvatarPath(options.Value, user.Id));
        if (user.HasAvatar)
        {
            user.HasAvatar = false;
            user.AvatarVersion++;
            await db.SaveChangesAsync();
            await hub.Clients.All.UserUpdated(user.ToDto());
        }

        return Results.Ok(user.ToDto());
    }

    private static async Task<IResult> Download(long id, HttpContext context, AppDbContext db, IOptions<DiscagemOptions> options)
    {
        var user = await db.Users.FindAsync(id);
        var path = AvatarPath(options.Value, id);
        if (user is not { HasAvatar: true } || !File.Exists(path))
        {
            return Results.NotFound();
        }

        var data = await File.ReadAllBytesAsync(path);
        var contentType = ContentType(data);
        if (contentType is null)
        {
            return Results.NotFound();
        }

        // Clients ask for /avatar?v={version}, so a cached copy never outlives a change.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Bytes(data, contentType);
    }

    private static string AvatarPath(DiscagemOptions options, long userId) => Path.Combine(DataPaths.Avatars(options), $"{userId}");

    /// <summary>Identifies PNG and JPEG data by its signature; anything else is refused.</summary>
    internal static string? ContentType(ReadOnlySpan<byte> data) =>
        data.StartsWith(PngSignature) ? "image/png" : data.StartsWith(JpegSignature) ? "image/jpeg" : null;
}
