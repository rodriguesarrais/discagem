using Discagem.Server.Data;
using Discagem.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Discagem.Server.Services;

/// <summary>
/// Periodically removes uploads that were never attached to a message, and files whose
/// database record no longer exists.
/// </summary>
public sealed class AttachmentCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<DiscagemOptions> options,
    TimeProvider time,
    ILogger<AttachmentCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Attachment cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = time.GetUtcNow().UtcDateTime - OrphanAge;

        var orphans = await db.Attachments
            .Where(a => a.MessageId == null && a.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        var uploads = DataPaths.Uploads(options.Value);
        foreach (var orphan in orphans)
        {
            TryDelete(Path.Combine(uploads, orphan.StorageName));
        }

        if (orphans.Count > 0)
        {
            db.Attachments.RemoveRange(orphans);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Removed {Count} unused uploads", orphans.Count);
        }

        var known = (await db.Attachments.Select(a => a.StorageName).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(uploads))
        {
            var info = new FileInfo(file);
            if (!known.Contains(info.Name) && info.LastWriteTimeUtc < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
