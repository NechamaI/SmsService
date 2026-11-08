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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad tick (e.g. DB briefly down) must not kill the worker.
                    logger.LogError(ex, "Sending batch failed; will retry on the next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
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
            await sender.SendAsync(message, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Message {Id}: {Status} after attempt {Attempts}",
                message.Id, message.Status, message.Attempts);
        }
    }
}
