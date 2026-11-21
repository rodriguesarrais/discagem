using Microsoft.EntityFrameworkCore;

namespace Discagem.Server.Data;

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations. Rooms, and their channels, are created by their members.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        // Rooms nobody is in: the room the AddRooms migration makes from the default channels of a
        // fresh database, which has no accounts yet. (A room is deleted when its last member leaves.)
        var empty = await db.Rooms.Where(r => !r.Members.Any()).ToListAsync();
        if (empty.Count > 0)
        {
            db.Rooms.RemoveRange(empty);
            await db.SaveChangesAsync();
        }
    }
}
