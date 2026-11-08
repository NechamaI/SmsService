using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmsService.Api.Data;
using SmsService.Api.Sending;
using SmsService.Tests.Fakes;

namespace SmsService.Tests.Api;

/// <summary>
/// Runs the whole API in memory, on SQLite in-memory instead of SQL Server, with a controllable clock.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // An in-memory SQLite database lives only as long as its connection is open,
    // so every DbContext must share this one connection.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ManualTimeProvider Time { get; } = new();

    public FakeSmsProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // No background sending in API tests: they check what the API stores, and messages must stay Pending.
        builder.UseSetting("Worker:Enabled", "false");

        builder.ConfigureTestServices(services =>
        {
            // Replace the SQL Server registration with SQLite.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            _connection.Open();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            // Never call a real provider from tests.
            services.RemoveAll<ISmsProvider>();
            services.AddSingleton<ISmsProvider>(Provider);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // No migrations here: they're written for SQL Server. Build the schema straight from the model.
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
