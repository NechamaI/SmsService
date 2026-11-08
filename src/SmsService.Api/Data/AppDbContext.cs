using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmsService.Api.Domain;

namespace SmsService.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<Message>();

        message.ToTable("Messages");
        message.HasKey(m => m.Id);

        message.Property(m => m.To).HasMaxLength(Message.ToMaxLength).IsRequired();
        message.Property(m => m.Body).HasMaxLength(Message.BodyMaxLength).IsRequired();

        // Enums are stored as text so the table is readable and reordering an enum doesn't corrupt data.
        message.Property(m => m.Encoding)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false);
        message.Property(m => m.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);

        message.Property(m => m.Attempts).HasDefaultValue(0);
        message.Property(m => m.LastError).HasMaxLength(Message.LastErrorMaxLength);
        message.Property(m => m.ProviderMessageId).HasMaxLength(Message.ProviderMessageIdMaxLength);

        // Worker: "Pending messages whose NextAttemptAt has passed".
        message.HasIndex(m => new { m.Status, m.NextAttemptAt });

        // List endpoint: "filter by status, newest first".
        message.HasIndex(m => new { m.Status, m.CreatedAt }).IsDescending(false, true);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // The database doesn't store DateTime.Kind. Mark every value read back as UTC,
        // so the API serializes it with a trailing "Z".
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
