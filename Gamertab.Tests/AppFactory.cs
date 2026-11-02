using Gamertab.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Gamertab.Tests;

public class AppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    protected virtual bool GoogleEnabled => false;
    protected virtual bool IgdbEnabled => false;

    public AppFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:Google:ClientId", GoogleEnabled ? "gamertab-test-client" : "");
        builder.UseSetting("Authentication:Google:ClientSecret", GoogleEnabled ? "gamertab-test-secret" : "");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewsUpdates:Enabled"] = "false",
            ["Igdb:ClientId"] = IgdbEnabled ? "igdb-test-client" : "",
            ["Igdb:ClientSecret"] = IgdbEnabled ? "igdb-test-secret" : "",
            ["Authentication:Google:ClientId"] = GoogleEnabled ? "gamertab-test-client" : "",
            ["Authentication:Google:ClientSecret"] = GoogleEnabled ? "gamertab-test-secret" : ""
        }));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            connection.Dispose();
    }
}
