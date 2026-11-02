using System.Text.Json;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace Gamertab.Tests;

public class McpToolsTests
{
    private class DatabaseFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task ToolsReadOnlyTheConfiguredAccountAndAddGamesWithoutChangingExistingEntries()
    {
        using var app = new AppFactory();
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Users.AddRange(new IdentityUser { Id = "mcp-owner", UserName = "mcp-owner" }, new IdentityUser { Id = "mcp-other", UserName = "mcp-other" });
        database.LibraryEntries.Add(new LibraryEntry { UserId = "mcp-other", GameId = 1, Status = PlayStatus.Completed });
        database.Collections.AddRange(new GameCollection { UserId = "mcp-owner", Name = "My shortlist", Items = [new CollectionItem { GameId = 1 }] },
            new GameCollection { UserId = "mcp-other", Name = "Private collection" });
        await database.SaveChangesAsync();
        var tools = new GamertabTools(new DatabaseFactory(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>()), new McpAccount("mcp-owner"));
        var title = await database.Games.Where(game => game.Id == 1).Select(game => game.Name).SingleAsync();
        Assert.Contains(title, await tools.SearchGames(title.ToLowerInvariant()));
        Assert.Equal("[]", await tools.GetLibrary());
        var collections = await tools.ListCollections();
        Assert.Contains("My shortlist", collections);
        Assert.DoesNotContain("Private collection", collections);
        var added = JsonSerializer.Deserialize<JsonElement>(await tools.AddToLibrary(1));
        Assert.True(added.GetProperty("added").GetBoolean());
        Assert.Equal("Planned", added.GetProperty("status").GetString());
        Assert.Contains(title, await tools.GetLibrary());
        var entry = await database.LibraryEntries.SingleAsync(entry => entry.UserId == "mcp-owner");
        entry.Status = PlayStatus.Playing;
        entry.Notes = "Keep these notes";
        entry.PersonalRating = 9;
        await database.SaveChangesAsync();
        var duplicate = JsonSerializer.Deserialize<JsonElement>(await tools.AddToLibrary(1));
        Assert.False(duplicate.GetProperty("added").GetBoolean());
        Assert.Equal("Playing", duplicate.GetProperty("status").GetString());
        await database.Entry(entry).ReloadAsync();
        Assert.Equal("Keep these notes", entry.Notes);
        Assert.Equal(9, entry.PersonalRating);
        Assert.Equal(2, await database.LibraryEntries.CountAsync());
    }

    [Fact]
    public async Task ToolsRejectInvalidLimitsAndUnknownGames()
    {
        using var app = new AppFactory();
        using var client = app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var tools = new GamertabTools(new DatabaseFactory(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>()), new McpAccount("unused"));
        await Assert.ThrowsAsync<McpException>(() => tools.SearchGames(new string('a', 121)));
        await Assert.ThrowsAsync<McpException>(() => tools.SearchGames("", 1000));
        await Assert.ThrowsAsync<McpException>(() => tools.GetLibrary(-1));
        await Assert.ThrowsAsync<McpException>(() => tools.ListCollections(0, 1000));
        await Assert.ThrowsAsync<McpException>(() => tools.AddToLibrary(int.MaxValue));
    }
}
