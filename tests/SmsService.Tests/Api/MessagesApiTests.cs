using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SmsService.Tests.Api;

/// <summary>
/// End-to-end tests of the HTTP API. xUnit creates a new instance per test,
/// so every test gets its own app and its own empty database.
/// </summary>
public sealed class MessagesApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public MessagesApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Post_valid_returns_202_with_location_and_get_returns_it()
    {
        var post = await _client.PostAsJsonAsync("/messages", new { to = "052-123-4567", body = "שלום" });

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var created = await ReadJson(post);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        Assert.EndsWith($"/messages/{id}", post.Headers.Location!.ToString());

        var get = await _client.GetAsync($"/messages/{id}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var message = await ReadJson(get);
        Assert.Equal("+972521234567", message.GetProperty("to").GetString());
        Assert.Equal("UCS2", message.GetProperty("encoding").GetString());
        Assert.Equal(1, message.GetProperty("segments").GetInt32());
        // Timestamps read back from the DB must still be marked UTC.
        Assert.EndsWith("Z", message.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task Post_invalid_returns_problem_details_per_field()
    {
        var response = await _client.PostAsJsonAsync("/messages", new { to = "123", body = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = (await ReadJson(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("to", out _));
        Assert.True(errors.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Post_empty_object_returns_400_not_500()
    {
        var response = await _client.PostAsync("/messages",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ReadJson(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("to", out _));
        Assert.True(errors.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Get_unknown_returns_404()
    {
        var response = await _client.GetAsync($"/messages/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_filters_by_status_newest_first()
    {
        var older = await ReadJson(await _client.PostAsJsonAsync("/messages", new { to = "0521234567", body = "first" }));
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        var newer = await ReadJson(await _client.PostAsJsonAsync("/messages", new { to = "0521234567", body = "second" }));

        var response = await _client.GetAsync("/messages?status=pending"); // lowercase is accepted

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ReadJson(response);
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items");
        Assert.Equal(newer.GetProperty("id").GetGuid(), items[0].GetProperty("id").GetGuid());
        Assert.Equal(older.GetProperty("id").GetGuid(), items[1].GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("status=Bogus")]
    [InlineData("status=1")]            // a number must not be read as an enum value
    [InlineData("status=Pending,Sent")] // Enum.TryParse would combine these into another status
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    public async Task List_rejects_bad_query(string query)
    {
        var response = await _client.GetAsync($"/messages?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
