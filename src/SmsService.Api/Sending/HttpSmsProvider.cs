using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmsService.Api.Sending;

/// <summary>Calls the SMS provider over HTTP and turns its answer into a <see cref="ProviderResult"/>.</summary>
public sealed class HttpSmsProvider(HttpClient http, ILogger<HttpSmsProvider> logger) : ISmsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record SendRequest(string To, string Text, Guid Reference);

    private sealed record SendResponse(string? Id);

    public async Task<ProviderResult> SendAsync(Guid reference, string to, string body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("sms", new SendRequest(to, body, reference), ct);

            if (response.IsSuccessStatusCode)
            {
                // 2xx means the provider accepted the SMS. Even if we can't read its id, retrying would send it again,
                // so this is still a success (with no ProviderMessageId).
                var id = TryReadId(await response.Content.ReadAsStringAsync(ct));
                if (id is null)
                {
                    logger.LogWarning("Provider accepted message {Reference} but returned no readable id", reference);
                }

                return ProviderResult.Ok(id);
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

    private static string? TryReadId(string body)
    {
        try
        {
            var id = JsonSerializer.Deserialize<SendResponse>(body, JsonOptions)?.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
