using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmsService.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    // Enums go over the wire as names ("Pending"), not numbers.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSwaggerGen();

// Code asks TimeProvider for "now" instead of DateTime.UtcNow, so tests can control time.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        sql => sql.EnableRetryOnFailure()));

var app = builder.Build();

// Swagger is enabled in all environments: the service runs locally only (no deploy), and the container runs as Production.
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

await DatabaseMigrator.MigrateAsync(app.Services, app.Lifetime.ApplicationStopping);

app.Run();

public partial class Program;
