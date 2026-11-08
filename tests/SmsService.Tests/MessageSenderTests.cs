using SmsService.Api.Domain;
using SmsService.Api.Sending;
using SmsService.Tests.Fakes;

namespace SmsService.Tests;

public sealed class MessageSenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeSmsProvider _provider = new();
    private readonly MessageSender _sender;

    public MessageSenderTests() => _sender = new MessageSender(_provider, new ManualTimeProvider(Now));

    // The worker hands MessageSender a message it has already marked Sending.
    private static Message NewMessage(int attempts = 0) => new()
    {
        Id = Guid.NewGuid(),
        To = "+972521234567",
        Body = "hello",
        Status = MessageStatus.Sending,
        Attempts = attempts,
        CreatedAt = Now.UtcDateTime.AddMinutes(-1),
        UpdatedAt = Now.UtcDateTime.AddMinutes(-1),
        NextAttemptAt = Now.UtcDateTime.AddMinutes(-1),
    };

    [Fact]
    public async Task Success_sets_Sent_with_SentAt_and_ProviderMessageId()
    {
        _provider.Result = ProviderResult.Ok("prov-1");
        var message = NewMessage();

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal(Now.UtcDateTime, message.SentAt);
        Assert.Equal("prov-1", message.ProviderMessageId);
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.LastError);
        Assert.Equal(Now.UtcDateTime, message.UpdatedAt);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    public async Task Transient_returns_to_Pending_with_backoff(int startAttempts, int expectedDelaySeconds)
    {
        _provider.Result = ProviderResult.Transient("HTTP 503");
        var message = NewMessage(startAttempts);

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.Equal(startAttempts + 1, message.Attempts);
        Assert.Equal(Now.UtcDateTime.AddSeconds(expectedDelaySeconds), message.NextAttemptAt);
        Assert.Equal("HTTP 503", message.LastError);
        Assert.Null(message.SentAt);
    }

    [Fact]
    public async Task Transient_on_fourth_attempt_fails()
    {
        _provider.Result = ProviderResult.Transient("503");
        var message = NewMessage(attempts: 3);

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal(4, message.Attempts);
    }

    [Fact]
    public async Task Permanent_error_fails_immediately()
    {
        _provider.Result = ProviderResult.Permanent("400 blocked");
        var message = NewMessage();

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal("400 blocked", message.LastError);
    }

    [Fact]
    public async Task Provider_exception_is_treated_as_transient()
    {
        _provider.ThrowOnSend = new InvalidOperationException("boom");
        var message = NewMessage();

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Contains("boom", message.LastError);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _provider.ThrowOnSend = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sender.SendAsync(NewMessage(), cts.Token));
    }

    [Fact]
    public async Task LastError_is_truncated_to_500()
    {
        _provider.Result = ProviderResult.Transient(new string('x', 600));
        var message = NewMessage();

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(Message.LastErrorMaxLength, message.LastError!.Length);
    }

    [Fact]
    public async Task Passes_id_to_and_body_to_provider()
    {
        var message = NewMessage();

        await _sender.SendAsync(message, CancellationToken.None);

        Assert.Equal((message.Id, "+972521234567", "hello"), _provider.LastSent);
    }
}
