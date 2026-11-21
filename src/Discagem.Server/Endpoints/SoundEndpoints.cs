using Discagem.Media.Audio;
using Discagem.Media.WebRtc;
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
/// Soundboard sounds. Clients decode whatever file the member picked and upload a short WAV file;
/// playing a sound only sends its id through the hub (see <see cref="ChatHub.PlaySound"/>), and every
/// client in the voice channel plays its own copy.
/// </summary>
public static class SoundEndpoints
{
    /// <summary>Leeway over <see cref="Limits.MaxSoundMilliseconds"/> for rounding in encoders.</summary>
    private const int DurationToleranceMs = 100;

    public static void MapSoundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sounds").RequireAuthorization();
        group.MapPost("/", Upload).DisableAntiforgery();
        group.MapGet("/{id:long}/audio", Download);
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
        var name = NormalizeName(form["name"].ToString());
        if (name.Length == 0)
        {
            return Results.BadRequest(new ApiError("Sounds need a name."));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new ApiError("No sound was uploaded."));
        }

        if (file.Length > Limits.MaxSoundBytes)
        {
            return Results.Json(new ApiError($"Sounds may be at most {Limits.MaxSoundBytes / 1024} KB."), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var data = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(data, cancellationToken);
        }

        int durationMs;
        try
        {
            var samples = WavAudio.ReadMono(data);
            durationMs = (int)(samples.LongLength * 1000 / DiscagemAudio.SampleRate);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new ApiError("Sounds must be WAV files."));
        }

        if (durationMs == 0)
        {
            return Results.BadRequest(new ApiError("That sound is empty."));
        }

        if (durationMs > Limits.MaxSoundMilliseconds + DurationToleranceMs)
        {
            return Results.BadRequest(new ApiError($"Sounds may be at most {Limits.MaxSoundMilliseconds / 1000} seconds long."));
        }

        if (await db.Sounds.CountAsync(cancellationToken) >= Limits.MaxSounds)
        {
            return Results.Conflict(new ApiError($"This server already has {Limits.MaxSounds} sounds. Remove one first."));
        }

        var sound = new Sound
        {
            Name = name,
            UploaderId = context.User.GetUserId(),
            DurationMs = Math.Min(durationMs, Limits.MaxSoundMilliseconds),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Sounds.Add(sound);
        await db.SaveChangesAsync(cancellationToken);

        if (!await StoredFiles.TryWriteAsync(AudioPath(options.Value, sound.Id), data, cancellationToken))
        {
            db.Sounds.Remove(sound);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Problem("The sound could not be saved.");
        }

        var dto = sound.ToDto();
        await hub.Clients.All.SoundCreated(dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> Download(long id, HttpContext context, AppDbContext db, IOptions<DiscagemOptions> options)
    {
        var path = AudioPath(options.Value, id);
        if (!await db.Sounds.AnyAsync(s => s.Id == id) || !File.Exists(path))
        {
            return Results.NotFound();
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(path, "audio/wav");
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext context,
        AppDbContext db,
        IOptions<DiscagemOptions> options,
        IHubContext<ChatHub, IChatClient> hub)
    {
        var sound = await db.Sounds.FindAsync(id);
        if (sound is null)
        {
            return Results.NotFound(new ApiError("That sound no longer exists."));
        }

        if (sound.UploaderId != context.User.GetUserId() && !context.User.IsAdmin())
        {
            return Results.Json(new ApiError("Only the person who added it or an administrator can remove it."), statusCode: StatusCodes.Status403Forbidden);
        }

        db.Sounds.Remove(sound);
        await db.SaveChangesAsync();
        AttachmentCleanupService.TryDelete(AudioPath(options.Value, id));
        await hub.Clients.All.SoundDeleted(id);
        return Results.NoContent();
    }

    /// <summary>Single-line names without control characters, at most <see cref="Limits.MaxSoundNameLength"/> long.</summary>
    internal static string NormalizeName(string? name)
    {
        var value = string.Join(' ', new string((name ?? "").Where(c => !char.IsControl(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (value.Length <= Limits.MaxSoundNameLength)
        {
            return value;
        }

        // Never cut an emoji (a surrogate pair) in half.
        var length = char.IsHighSurrogate(value[Limits.MaxSoundNameLength - 1]) ? Limits.MaxSoundNameLength - 1 : Limits.MaxSoundNameLength;
        return value[..length].TrimEnd();
    }

    private static string AudioPath(DiscagemOptions options, long id) => Path.Combine(DataPaths.Sounds(options), $"{id}.wav");
}
