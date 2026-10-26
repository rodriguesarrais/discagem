using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Discagem.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no time zone support: store UTC and restore DateTimeKind.Utc when reading.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.NormalizedUsername).IsUnique();
            user.Property(u => u.Username).HasMaxLength(64);
            user.Property(u => u.NormalizedUsername).HasMaxLength(64);
            user.Property(u => u.DisplayName).HasMaxLength(64);
            user.Property(u => u.AvatarColor).HasMaxLength(16);
        });

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.Property(c => c.Name).HasMaxLength(64);
            channel.Property(c => c.Topic).HasMaxLength(512);
        });

        modelBuilder.Entity<Message>(message =>
        {
            message.HasIndex(m => new { m.ChannelId, m.Id });
            message.HasOne(m => m.Channel).WithMany(c => c.Messages).HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Cascade);
            message.HasOne(m => m.Author).WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Attachment>(attachment =>
        {
            attachment.HasOne(a => a.Message).WithMany(m => m.Attachments).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.SetNull);
            attachment.Property(a => a.FileName).HasMaxLength(256);
            attachment.Property(a => a.ContentType).HasMaxLength(128);
            attachment.Property(a => a.StorageName).HasMaxLength(128);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
