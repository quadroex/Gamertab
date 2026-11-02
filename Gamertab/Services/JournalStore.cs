using System.Text.Json;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class JournalStore(AppDbContext database)
{
    public Task<bool> CanReadAsync(string userId, int gameId) =>
        database.LibraryEntries.AnyAsync(entry => entry.UserId == userId && entry.GameId == gameId);

    public async Task<JournalSnapshot> ReadAsync(string userId, int gameId)
    {
        var journal = await database.PlayJournals.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId && item.GameId == gameId);
        return journal is null ? new(0, [new("paragraph", "")], userId) : new(journal.Revision, JsonSerializer.Deserialize<List<JournalBlock>>(journal.BlocksJson)!, userId);
    }

    public async Task<bool> SaveAsync(string userId, int gameId, JournalInput input)
    {
        var json = JsonSerializer.Serialize(input.Blocks);
        if (input.Revision == 0)
            return await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PlayJournals" ("UserId", "GameId", "Revision", "BlocksJson")
                VALUES ({userId}, {gameId}, 1, {json}) ON CONFLICT ("UserId", "GameId") DO NOTHING
                """) == 1;
        return await database.PlayJournals.Where(item => item.UserId == userId && item.GameId == gameId && item.Revision == input.Revision)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.BlocksJson, json).SetProperty(item => item.Revision, input.Revision + 1)) == 1;
    }
}
