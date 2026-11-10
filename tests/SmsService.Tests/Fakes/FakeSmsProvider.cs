using SmsService.Api.Sending;

namespace SmsService.Tests.Fakes;

/// <summary>A provider that returns a configured result (or throws), and remembers what it was asked to send.</summary>
public sealed class FakeSmsProvider : ISmsProvider
{
    public ProviderResult Result { get; set; } = ProviderResult.Ok("fake-id");
    public Exception? ThrowOnSend { get; set; }

    /// <summary>Runs inside SendAsync, e.g. to simulate shutdown starting while the provider is answering.</summary>
    public Action? OnSend { get; set; }

    public (Guid Reference, string To, string Body)? LastSent { get; private set; }

    public Task<ProviderResult> SendAsync(Guid reference, string to, string body, CancellationToken ct)
    {
        LastSent = (reference, to, body);
        OnSend?.Invoke();
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        return Task.FromResult(Result);
    }
}
