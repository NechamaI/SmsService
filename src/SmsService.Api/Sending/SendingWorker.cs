using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsService.Api.Data;
using SmsService.Api.Domain;

namespace SmsService.Api.Sending;

/// <summary>Background loop: every second, picks up due Pending messages and sends them.</summary>
public sealed class SendingWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<WorkerOptions> options,
    ILogger<SendingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After this long in Sending, a message is treated as abandoned. Must be well above the longest legitimate
    /// wait inside one batch: BatchSize × provider timeout (20 × 5s = 100s).
    /// </summary>
    internal static readonly TimeSpan StaleSendingAfter = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One pass of the loop. Internal so tests can drive it without waiting on the timer.</summary>
    internal async Task RunTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ProcessBatchAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Only a real shutdown may end the loop. Anything else, including a cancellation that isn't ours
            // (e.g. a DB command timeout), is logged and retried next tick; otherwise one bad tick kills the worker.
            logger.LogError(ex, "Sending batch failed; will retry on the next tick");
        }
    }

    /// <summary>
    /// Messages left as Sending were mid-send when the service stopped. We can't know if the provider got them,
    /// so they go back to Pending (possible duplicate send, accepted and documented).
    /// </summary>
    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow().UtcDateTime;

        var count = await db.Messages
            .Where(m => m.Status == MessageStatus.Sending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Pending)
                .SetProperty(m => m.UpdatedAt, now), ct);

        if (count > 0)
        {
            logger.LogWarning("Returned {Count} interrupted Sending messages to Pending", count);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<MessageSender>();
        var now = time.GetUtcNow().UtcDateTime;

        await RecoverStaleAsync(db, now, ct);

        var batch = await db.Messages
            .Where(m => m.Status == MessageStatus.Pending && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt)
            .Take(options.Value.BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            return;
        }

        // Save Sending BEFORE calling the provider, so a crash mid-send leaves a trace we can recover from.
        foreach (var message in batch)
        {
            message.Status = MessageStatus.Sending;
            message.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        foreach (var message in batch)
        {
            // Don't start new sends once shutdown begins; the rest stay Sending and are recovered on startup.
            ct.ThrowIfCancellationRequested();

            await sender.SendAsync(message, ct);

            // The provider has already answered, so save the outcome even if shutdown started meanwhile.
            // Cancelling this save would leave a delivered SMS as Sending, and it would be sent again after restart.
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Message {Id}: {Status} after attempt {Attempts}",
                message.Id, message.Status, message.Attempts);
        }
    }

    /// <summary>
    /// A message can be left in Sending without a restart, e.g. when a save fails half-way through a batch.
    /// Anything in Sending for longer than <see cref="StaleSendingAfter"/> is returned to Pending.
    /// </summary>
    private async Task RecoverStaleAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var staleBefore = now - StaleSendingAfter;
        var count = await db.Messages
            .Where(m => m.Status == MessageStatus.Sending && m.UpdatedAt < staleBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Pending)
                .SetProperty(m => m.UpdatedAt, now), ct);

        if (count > 0)
        {
            logger.LogWarning("Returned {Count} stale Sending messages to Pending", count);
        }
    }
}
