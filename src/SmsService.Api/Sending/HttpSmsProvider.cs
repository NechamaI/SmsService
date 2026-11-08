using System.Net;
using System.Net.Http.Json;

namespace SmsService.Api.Sending;

/// <summary>Calls the SMS provider over HTTP and turns its answer into a <see cref="ProviderResult"/>.</summary>
public sealed class HttpSmsProvider(HttpClient http) : ISmsProvider
{
    private sealed record SendRequest(string To, string Text, Guid Reference);

    private sealed record SendResponse(string? Id);

    public async Task<ProviderResult> SendAsync(Guid reference, string to, string body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("sms", new SendRequest(to, body, reference), ct);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<SendResponse>(ct);
                return string.IsNullOrEmpty(payload?.Id)
                    ? ProviderResult.Transient("Provider returned success without an id")
                    : ProviderResult.Ok(payload.Id);
            }

            var code = (int)response.StatusCode;
            var error = $"HTTP {code}: {await response.Content.ReadAsStringAsync(ct)}";

            // 408 and 429 are 4xx but worth retrying: the request itself was fine.
            var transient = code >= 500
                || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

            return transient ? ProviderResult.Transient(error) : ProviderResult.Permanent(error);
        }
        catch (HttpRequestException ex)
        {
            return ProviderResult.Transient($"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired, not a shutdown. The provider may have got the message anyway (documented risk).
            return ProviderResult.Transient($"Timeout after {http.Timeout.TotalSeconds:0}s");
        }
    }
}
