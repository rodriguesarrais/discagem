using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Hubs;
using Discagem.Server.Options;
using Discagem.Server.Services;
using Discagem.Shared;
using Discagem.Shared.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

/// <summary>
/// Custom emojis and stickers. Any member can add them; the uploader and administrators can remove
/// them. Clients scale pictures and encode them as PNG before uploading, so the server only accepts
/// small PNG or JPEG files.
/// </summary>
public static class EmojiEndpoints
{
    public static void MapEmojiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/emojis").RequireAuthorization();
        group.MapPost("/", Upload).DisableAntiforgery();
        group.MapGet("/{id:long}/image", Download);
        group.MapDelete("/{id:long}", Delete);
    }

    private static async Task<IResult> Upload(
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new ApiError("Expected a multipart/form-data request."));
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!Enum.TryParse<EmojiKind>(form["kind"].ToString(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            kind = EmojiKind.Emoji;
        }

        var noun = kind == EmojiKind.Sticker ? "Stickers" : "Emojis";
        if (!EmojiNames.TryNormalize(form["name"].ToString(), out var name))
        {
            return Results.BadRequest(new ApiError(
                $"Names must have {Limits.MinEmojiNameLength} to {Limits.MaxEmojiNameLength} letters, digits or underscores."));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ApiError("No picture was uploaded."));
        }

        var maxBytes = kind == EmojiKind.Sticker ? Limits.MaxStickerBytes : Limits.MaxEmojiBytes;
        if (file.Length > maxBytes)
        {
            return Results.Json(new ApiError($"{noun} may be at most {maxBytes / 1024} KB."), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var data = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(data, cancellationToken);
        }

        if (AvatarEndpoints.ContentType(data) is null)
        {
            return Results.BadRequest(new ApiError($"{noun} must be PNG or JPEG images."));
        }

        var max = kind == EmojiKind.Sticker ? Limits.MaxStickers : Limits.MaxEmojis;
        if (await db.Emojis.CountAsync(e => e.Kind == kind, cancellationToken) >= max)
        {
            return Results.Conflict(new ApiError($"This server already has {max} {noun.ToLowerInvariant()}. Remove one first."));
        }

        if (await db.Emojis.AnyAsync(e => e.Name == name, cancellationToken))
        {
            return Results.Conflict(new ApiError($"There already is an emoji or sticker called :{name}:."));
        }

        var emoji = new CustomEmoji
        {
            Name = name,
            Kind = kind,
            UploaderId = context.User.GetUserId(),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Emojis.Add(emoji);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone took the name a moment ago (the name is unique in the database).
            return Results.Conflict(new ApiError($"There already is an emoji or sticker called :{name}:."));
        }

        if (!await StoredFiles.TryWriteAsync(ImagePath(options.Value, emoji.Id), data, cancellationToken))
        {
            db.Emojis.Remove(emoji);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Problem("The picture could not be saved.");
        }

        var dto = emoji.ToDto();
        await hub.Clients.All.EmojiCreated(dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> Download(long id, HttpContext context, AppDbContext db, IOptions<DiscagemOptions> options)
    {
        var path = ImagePath(options.Value, id);
        if (!await db.Emojis.AnyAsync(e => e.Id == id) || !File.Exists(path))
        {
            return Results.NotFound();
        }

        var data = await File.ReadAllBytesAsync(path);
        var contentType = AvatarEndpoints.ContentType(data);
        if (contentType is null)
        {
            return Results.NotFound();
        }

        // A picture never changes: a new one is a new emoji with a new id.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Bytes(data, contentType);
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub)
    {
        var emoji = await db.Emojis.FindAsync(id);
        if (emoji is null)
        {
            return Results.NotFound(new ApiError("That emoji no longer exists."));
        }

        if (emoji.UploaderId != context.User.GetUserId() && !context.User.IsAdmin())
        {
            return Results.Json(new ApiError("Only the person who added it or an administrator can remove it."), statusCode: StatusCodes.Status403Forbidden);
        }

        db.Emojis.Remove(emoji);
        await db.SaveChangesAsync();
        AttachmentCleanupService.TryDelete(ImagePath(options.Value, id));
        await hub.Clients.All.EmojiDeleted(id);
        return Results.NoContent();
    }

    private static string ImagePath(DiscagemOptions options, long id) => Path.Combine(DataPaths.Emojis(options), $"{id}");
}

/// <summary>Writes uploaded files so that a failure never leaves half a file behind.</summary>
internal static class StoredFiles
{
    public static async Task<bool> TryWriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AttachmentCleanupService.TryDelete(temporary);
            return false;
        }
    }
}
