using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmsService.Api.Data;
using SmsService.Api.Domain;
using SmsService.Api.Sending;
using SmsService.Tests.Fakes;

namespace SmsService.Tests;

/// <summary>
/// Drives one worker tick at a time (RunTickAsync) against SQLite in-memory, with a fake provider and clock.
/// </summary>
public sealed class SendingWorkerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeSmsProvider _provider = new();
    private readonly FailOnceSaveInterceptor _failingSave = new();
    private readonly ServiceProvider _services;
    private readonly SendingWorker _worker;

    public SendingWorkerTests()
    {
        _connection.Open();
        var time = new ManualTimeProvider(new DateTimeOffset(Now));

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_failingSave));
        services.AddSingleton<ISmsProvider>(_provider);
        services.AddSingleton<TimeProvider>(time);
        services.AddScoped<MessageSender>();
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        _worker = new SendingWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            time,
            Options.Create(new WorkerOptions()),
            NullLogger<SendingWorker>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private Guid Seed(MessageStatus status, DateTime updatedAt)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            To = "+972521234567",
            Body = "hello",
            Status = status,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            NextAttemptAt = updatedAt,
        };
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Messages.Add(message);
        db.SaveChanges();
        return message.Id;
    }

    private Message Load(Guid id)
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Messages.AsNoTracking().Single(m => m.Id == id);
    }

    [Fact]
    public async Task Message_stuck_in_Sending_for_long_is_recovered_and_sent()
    {
        // Left behind by a batch whose save failed half-way; nothing restarted the service.
        var id = Seed(MessageStatus.Sending, Now.AddMinutes(-10));

        await _worker.RunTickAsync(CancellationToken.None);

        Assert.Equal(MessageStatus.Sent, Load(id).Status);
    }

    [Fact]
    public async Task Message_in_Sending_recently_is_left_alone()
    {
        // Could still be waiting for its turn inside a running batch.
        var id = Seed(MessageStatus.Sending, Now.AddMinutes(-1));

        await _worker.RunTickAsync(CancellationToken.None);

        Assert.Equal(MessageStatus.Sending, Load(id).Status);
        Assert.Null(_provider.LastSent);
    }

    [Fact]
    public async Task Provider_answer_is_saved_even_if_shutdown_starts_during_the_send()
    {
        var id = Seed(MessageStatus.Pending, Now.AddSeconds(-1));
        using var shutdown = new CancellationTokenSource();
        _provider.Result = ProviderResult.Ok("prov-1");
        _provider.OnSend = shutdown.Cancel; // the provider accepted the SMS, then shutdown began

        await Record.ExceptionAsync(() => _worker.RunTickAsync(shutdown.Token));

        var message = Load(id);
        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal("prov-1", message.ProviderMessageId);
    }

    [Fact]
    public async Task Cancellation_that_is_not_a_shutdown_does_not_escape_the_tick()
    {
        Seed(MessageStatus.Pending, Now.AddSeconds(-1));
        // E.g. a DB command timeout surfacing as a cancellation while the service keeps running.
        _failingSave.ThrowOnNextSave = new OperationCanceledException();

        var escaped = await Record.ExceptionAsync(() => _worker.RunTickAsync(CancellationToken.None));

        Assert.Null(escaped);
    }

    /// <summary>Makes the next SaveChanges throw a given exception, once.</summary>
    private sealed class FailOnceSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? ThrowOnNextSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextSave is { } ex)
            {
                ThrowOnNextSave = null;
                throw ex;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
