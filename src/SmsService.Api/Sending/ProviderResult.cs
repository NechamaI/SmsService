namespace SmsService.Api.Sending;

/// <summary>What happened when we handed a message to the SMS provider.</summary>
public sealed record ProviderResult(bool Success, bool IsTransient, string? ProviderMessageId, string? Error)
{
    public static ProviderResult Ok(string providerMessageId) => new(true, false, providerMessageId, null);

    /// <summary>A failure that may go away on retry (timeout, 408, 429, 5xx).</summary>
    public static ProviderResult Transient(string error) => new(false, true, null, error);

    /// <summary>A failure that will repeat on retry (other 4xx).</summary>
    public static ProviderResult Permanent(string error) => new(false, false, null, error);
}
