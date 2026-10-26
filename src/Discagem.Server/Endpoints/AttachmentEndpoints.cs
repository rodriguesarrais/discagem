using Discagem.Server.Auth;
using Discagem.Server.Data;
using Discagem.Server.Options;
using Discagem.Shared;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Endpoints;

public static class AttachmentEndpoints
{
    /// <summary>Image types the server is willing to serve inline. Everything else is served as a download.</summary>
    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
    };

    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/attachments").RequireAuthorization();
        group.MapPost("/", Upload).DisableAntiforgery();
        group.MapGet("/{id:long}/{fileName?}", Download);
    }

    private static async Task<IResult> Upload(
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new ApiError("Expected a multipart/form-data request."));
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ApiError("No file was uploaded."));
        }

        if (file.Length > options.Value.MaxUploadBytes)
        {
            return Results.Json(
                new ApiError($"Files may be at most {options.Value.MaxUploadBytes / (1024 * 1024)} MB."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var storageName = Guid.NewGuid().ToString("N");
        var path = Path.Combine(DataPaths.Uploads(options.Value), storageName);
        await using (var target = File.Create(path))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        var contentType = NormalizeContentType(file.ContentType, file.FileName);
        var attachment = new Attachment
        {
            UploaderId = context.User.GetUserId(),
            FileName = SanitizeFileName(file.FileName),
            ContentType = contentType,
            Size = file.Length,
            StorageName = storageName,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(attachment.ToDto());
    }

    private static async Task<IResult> Download(long id, HttpContext context, AppDbContext db, IOptions<DiscagemOptions> options)
    {
        var attachment = await db.Attachments.FindAsync(id);

        // Uploads that have not been posted yet are only visible to their uploader.
        if (attachment is null || (attachment.MessageId is null && attachment.UploaderId != context.User.GetUserId()))
        {
            return Results.NotFound();
        }

        var path = Path.Combine(DataPaths.Uploads(options.Value), attachment.StorageName);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        if (InlineImageTypes.Contains(attachment.ContentType))
        {
            return Results.File(path, attachment.ContentType, enableRangeProcessing: true);
        }

        return Results.File(path, "application/octet-stream", attachment.FileName, enableRangeProcessing: true);
    }

    internal static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        name = new string(name.Where(c => !char.IsControl(c) && c != '"' && c != '\\' && c != '/').ToArray()).Trim();
        if (name.Length == 0)
        {
            return "file";
        }

        if (name.Length > 200)
        {
            var extension = Path.GetExtension(name);
            name = name[..(200 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
        }

        return name;
    }

    private static string NormalizeContentType(string? contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var byExtension = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".txt" or ".log" or ".md" => "text/plain",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".mp4" => "video/mp4",
            _ => null,
        };

        if (byExtension is not null)
        {
            return byExtension;
        }

        if (!string.IsNullOrWhiteSpace(contentType) && contentType.Length <= 100 && !contentType.Contains(';') &&
            !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return contentType.Trim().ToLowerInvariant();
        }

        return "application/octet-stream";
    }
}
