using SmsService.Api.Domain;

namespace SmsService.Api.Sending;

/// <summary>
/// Makes one send attempt and decides the message's next state. Pure logic: it changes the
/// <see cref="Message"/> in memory and never touches the database (the worker saves it).
/// </summary>
public sealed class MessageSender(ISmsProvider provider, TimeProvider time)
{
    public const int MaxAttempts = 4;

    // Wait before the next try, indexed by Attempts - 1 (after the increment).
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    public async Task SendAsync(Message message, CancellationToken ct)
    {
        ProviderResult result;
        try
        {
            result = await provider.SendAsync(message.Id, message.To, message.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave the message as Sending; it goes back to Pending on the next startup.
            throw;
        }
        catch (Exception ex)
        {
            // Anything unexpected from the provider is treated as temporary.
            result = ProviderResult.Transient(ex.Message);
        }

        var now = time.GetUtcNow().UtcDateTime;
        message.Attempts++; // counts every call to the provider, whatever the outcome
        message.UpdatedAt = now;

        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
            message.SentAt = now;
            message.ProviderMessageId = result.ProviderMessageId;
            message.LastError = null;
            return;
        }

        message.LastError = Truncate(result.Error ?? "Unknown error", Message.LastErrorMaxLength);

        if (result.IsTransient && message.Attempts < MaxAttempts)
        {
            message.Status = MessageStatus.Pending;
            message.NextAttemptAt = now + Backoff[message.Attempts - 1];
        }
        else
        {
            message.Status = MessageStatus.Failed;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
