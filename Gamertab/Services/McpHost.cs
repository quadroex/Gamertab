using Gamertab.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Npgsql;

namespace Gamertab.Services;

public record McpAccount(string UserId);

public static class McpHost
{
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--mcp").ToArray());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var connectionString = builder.Configuration.GetConnectionString("Gamertab");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var environmentFile = builder.Configuration["Mcp:EnvironmentFile"] ?? Path.Combine(Environment.CurrentDirectory, ".env");
            var passwordLine = File.ReadLines(environmentFile).FirstOrDefault(line => line.StartsWith("POSTGRES_PASSWORD="));
            if (passwordLine is null) throw new InvalidOperationException("Set ConnectionStrings__Gamertab or provide the local .env file.");
            connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = 15432, Database = "gamertab", Username = "gamertab",
                Password = passwordLine["POSTGRES_PASSWORD=".Length..]
            }.ConnectionString;
        }
        builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connectionString));
        var email = builder.Configuration["Mcp:AccountEmail"];
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("Set Mcp__AccountEmail to an existing Gamertab account email.");
        await using (var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options))
        {
            var normalizedEmail = email.Trim().ToUpperInvariant();
            var userId = await database.Users.Where(user => user.NormalizedEmail == normalizedEmail).Select(user => user.Id).SingleOrDefaultAsync();
            if (userId is null) throw new InvalidOperationException("The configured Gamertab account does not exist. Register on the website first.");
            builder.Services.AddSingleton(new McpAccount(userId));
        }
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<GamertabTools>();
        await builder.Build().RunAsync();
    }
}
