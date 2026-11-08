using Microsoft.EntityFrameworkCore;

namespace SmsService.Api.Data;

public static class DatabaseMigrator
{
    private const int MaxTries = 10;
    private static readonly TimeSpan DelayBetweenTries = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Applies pending migrations on startup. SQL Server can report healthy before it accepts
    /// connections, so this retries a few times before giving up.
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));

        // Tests run on SQLite and create the schema with EnsureCreated instead.
        if (!db.Database.IsSqlServer())
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(ct);
                logger.LogInformation("Database migrations applied");
                return;
            }
            catch (Exception ex) when (attempt < MaxTries)
            {
                logger.LogWarning(ex, "Migration attempt {Attempt}/{MaxTries} failed, retrying in {Delay}s",
                    attempt, MaxTries, DelayBetweenTries.TotalSeconds);
                await Task.Delay(DelayBetweenTries, ct);
            }
        }
    }
}
