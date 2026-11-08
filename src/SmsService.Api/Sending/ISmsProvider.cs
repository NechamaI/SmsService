namespace SmsService.Api.Sending;

public interface ISmsProvider
{
    /// <param name="reference">Our message id, sent along so the provider can deduplicate retries.</param>
    Task<ProviderResult> SendAsync(Guid reference, string to, string body, CancellationToken ct);
}
