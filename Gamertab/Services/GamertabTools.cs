using System.ComponentModel;
using System.Text.Json;
using Gamertab.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Gamertab.Services;

[McpServerToolType]
public class GamertabTools(IDbContextFactory<AppDbContext> databases, McpAccount account)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    [McpServerTool(Name = "search_games", ReadOnly = true, OpenWorld = false), Description("Search Gamertab's saved game catalogue by title. Returns local game IDs for add_to_library. This does not search the live IGDB catalogue.")]
    public async Task<string> SearchGames([Description("Part of the game title, up to 120 characters.")] string query = "", [Description("Maximum results, from 1 to 20.")] int limit = 10)
    {
        if (query is null || query.Length > 120 || limit is < 1 or > 20) throw new McpException("Use a title up to 120 characters and a limit from 1 to 20.");
        await using var database = await databases.CreateDbContextAsync();
        var term = query.Trim().ToLowerInvariant();
        return Json(await database.Games.AsNoTracking().Where(game => game.Name.ToLower().Contains(term)).OrderBy(game => game.Name)
            .Take(limit).Select(game => new { game.Id, game.Name, game.Genres, game.ReleasedOn }).ToListAsync());
    }

    [McpServerTool(Name = "get_library", ReadOnly = true, OpenWorld = false), Description("Read the configured account's private game library, statuses and personal ratings. No other account can be selected.")]
    public async Task<string> GetLibrary([Description("Items to skip, from 0 to 100000.")] int offset = 0, [Description("Page size, from 1 to 100.")] int limit = 20)
    {
        if (offset is < 0 or > 100000 || limit is < 1 or > 100) throw new McpException("Use an offset from 0 to 100000 and a limit from 1 to 100.");
        await using var database = await databases.CreateDbContextAsync();
        return Json(await database.LibraryEntries.AsNoTracking().Where(entry => entry.UserId == account.UserId).OrderBy(entry => entry.Id)
            .Skip(offset).Take(limit).Select(entry => new { entry.Id, entry.GameId, gameName = entry.Game.Name, status = entry.Status.ToString(), entry.PersonalRating }).ToListAsync());
    }

    [McpServerTool(Name = "list_collections", ReadOnly = true, OpenWorld = false), Description("Read collection names and game counts belonging to the configured account.")]
    public async Task<string> ListCollections([Description("Items to skip, from 0 to 100000.")] int offset = 0, [Description("Page size, from 1 to 50.")] int limit = 20)
    {
        if (offset is < 0 or > 100000 || limit is < 1 or > 50) throw new McpException("Use an offset from 0 to 100000 and a limit from 1 to 50.");
        await using var database = await databases.CreateDbContextAsync();
        return Json(await database.Collections.AsNoTracking().Where(collection => collection.UserId == account.UserId).OrderBy(collection => collection.Id)
            .Skip(offset).Take(limit).Select(collection => new { collection.Id, collection.Name, gameCount = collection.Items.Count }).ToListAsync());
    }

    [McpServerTool(Name = "add_to_library", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Add a saved catalogue game to the configured account's library as Planned. Only call when the user asks to add it. Existing entries, statuses, notes and ratings are preserved.")]
    public async Task<string> AddToLibrary([Description("Local game ID returned by search_games, not a Steam or IGDB ID.")] int gameId)
    {
        await using var database = await databases.CreateDbContextAsync();
        var game = await database.Games.AsNoTracking().FirstOrDefaultAsync(game => game.Id == gameId);
        if (game is null) throw new McpException("Game not found. Search the saved catalogue first.");
        var added = await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LibraryEntries" ("UserId", "GameId", "Status", "Notes", "FollowNews")
            VALUES ({account.UserId}, {gameId}, 0, {""}, {true}) ON CONFLICT ("UserId", "GameId") DO NOTHING
            """) == 1;
        var entry = await database.LibraryEntries.AsNoTracking().SingleAsync(entry => entry.UserId == account.UserId && entry.GameId == gameId);
        return Json(new { added, entry.Id, entry.GameId, gameName = game.Name, status = entry.Status.ToString() });
    }
}
