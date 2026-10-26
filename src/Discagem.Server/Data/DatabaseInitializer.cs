using Microsoft.EntityFrameworkCore;

namespace Discagem.Server.Data;

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations and seeds the default channel.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (!await db.Channels.AnyAsync())
        {
            db.Channels.Add(new Channel
            {
                Name = "general",
                Topic = "Say hello!",
                Position = 0,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }
    }
}
