using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmsService.Api.Sending;

namespace SmsService.Tests;

/// <summary>Checks how HTTP answers from the provider map to success / transient / permanent.</summary>
public sealed class HttpSmsProviderTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (HttpSmsProvider Provider, StubHandler Handler) Create(HttpStatusCode status, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://provider/") };
        return (new HttpSmsProvider(http, NullLogger<HttpSmsProvider>.Instance), handler);
    }

    [Fact]
    public async Task Ok_returns_provider_id_and_sends_to_text_reference()
    {
        var (provider, handler) = Create(HttpStatusCode.OK, """{"id":"prov-1"}""");
        var reference = Guid.NewGuid();

        var result = await provider.SendAsync(reference, "+972521234567", "hi", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("prov-1", result.ProviderMessageId);
        var sent = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("+972521234567", sent.GetProperty("to").GetString());
        Assert.Equal("hi", sent.GetProperty("text").GetString());
        Assert.Equal(reference, sent.GetProperty("reference").GetGuid());
    }

    [Theory]
    [InlineData("{}")]          // no id field
    [InlineData("")]            // empty body
    [InlineData("not json")]    // unreadable body
    public async Task Success_without_a_readable_id_is_still_success(string body)
    {
        // The provider accepted the SMS. Retrying would send it again, so this must not be an error.
        var (provider, _) = Create(HttpStatusCode.OK, body);

        var result = await provider.SendAsync(Guid.NewGuid(), "+972521234567", "hi", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ProviderMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]      // 408
    [InlineData(HttpStatusCode.TooManyRequests, true)]     // 429
    [InlineData(HttpStatusCode.InternalServerError, true)] // 500
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]  // 503
    [InlineData(HttpStatusCode.BadRequest, false)]         // 400
    [InlineData(HttpStatusCode.Unauthorized, false)]       // 401
    public async Task Error_codes_map_to_transient_or_permanent(HttpStatusCode status, bool expectedTransient)
    {
        var (provider, _) = Create(status, """{"error":"nope"}""");

        var result = await provider.SendAsync(Guid.NewGuid(), "+972521234567", "hi", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedTransient, result.IsTransient);
        Assert.Contains(((int)status).ToString(), result.Error);
    }
}
