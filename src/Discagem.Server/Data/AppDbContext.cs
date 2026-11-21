using Discagem.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Discagem.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();

    public DbSet<CustomEmoji> Emojis => Set<CustomEmoji>();

    public DbSet<Sound> Sounds => Set<Sound>();

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

        modelBuilder.Entity<Room>(room =>
        {
            room.Property(r => r.Name).HasMaxLength(Limits.MaxRoomNameLength);
            room.Property(r => r.Color).HasMaxLength(16);
            room.Property(r => r.InviteCode).HasMaxLength(32);
            room.HasIndex(r => r.InviteCode).IsUnique();
            room.HasOne<User>().WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.SetNull);
            room.HasMany(r => r.Channels).WithOne(c => c.Room).HasForeignKey(c => c.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoomMember>(member =>
        {
            member.HasKey(m => new { m.RoomId, m.UserId });
            member.HasIndex(m => m.UserId);
            member.HasOne(m => m.Room).WithMany(r => r.Members).HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomEmoji>(emoji =>
        {
            emoji.ToTable("Emojis");
            emoji.HasIndex(e => e.Name).IsUnique();
            emoji.Property(e => e.Name).HasMaxLength(32);
            emoji.HasOne<User>().WithMany().HasForeignKey(e => e.UploaderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Sound>(sound =>
        {
            sound.Property(s => s.Name).HasMaxLength(32);
            sound.HasOne<User>().WithMany().HasForeignKey(s => s.UploaderId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
