using System.Globalization;

// A stand-in for a real SMS provider: slow, sometimes down, and it rejects one kind of number.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Share of requests that get 503, to exercise retries. InvariantCulture so "0.15" parses on any machine locale.
var failRate = double.TryParse(
    Environment.GetEnvironmentVariable("FAIL_RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
    ? rate
    : 0.15;
app.Logger.LogInformation("Mock SMS provider started with FAIL_RATE={FailRate}", failRate);

app.MapPost("/sms", async (SendSmsRequest request, ILogger<Program> logger) =>
{
    // Real providers aren't instant.
    await Task.Delay(Random.Shared.Next(100, 801));

    if (string.IsNullOrWhiteSpace(request.To) || string.IsNullOrWhiteSpace(request.Text))
    {
        logger.LogInformation("ref={Reference} -> 400 missing to/text", request.Reference);
        return Results.BadRequest(new { error = "Fields 'to' and 'text' are required" });
    }

    // Lets us demo a permanent failure on demand.
    if (request.To.EndsWith("999", StringComparison.Ordinal))
    {
        logger.LogInformation("ref={Reference} to={To} -> 400 recipient blocked", request.Reference, request.To);
        return Results.BadRequest(new { error = "Recipient blocked" });
    }

    if (Random.Shared.NextDouble() < failRate)
    {
        logger.LogInformation("ref={Reference} to={To} -> 503 simulated outage", request.Reference, request.To);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var id = Guid.NewGuid();
    logger.LogInformation("ref={Reference} to={To} -> 200 id={Id}", request.Reference, request.To, id);
    return Results.Ok(new { id });
});

app.MapGet("/health", () => Results.Ok());

app.Run();

internal sealed record SendSmsRequest(string? To, string? Text, Guid? Reference);
