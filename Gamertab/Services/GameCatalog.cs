using Gamertab.Data;
using Gamertab.Models;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class GameCatalog(AppDbContext database, IgdbClient igdb, IOutputCacheStore outputCache, GameSearchIndex searchIndex, ILogger<GameCatalog> logger)
{
    public const string CacheTag = "games";

    public async Task<CatalogPage> BrowseAsync(string? query, string? genre, int page, CancellationToken cancellationToken)
    {
        string? notice = null;
        if (igdb.IsConfigured)
        {
            try
            {
                var results = await igdb.BrowseAsync(query, genre, page, cancellationToken);
                var igdbIds = results.Select(game => game.IgdbId).ToArray();
                var steamIds = results.Where(game => game.SteamAppId.HasValue).Select(game => game.SteamAppId).ToArray();
                var saved = await database.Games.AsNoTracking()
                    .Where(game => igdbIds.Contains(game.IgdbId) || (game.SteamAppId.HasValue && steamIds.Contains(game.SteamAppId)))
                    .ToListAsync(cancellationToken);
                foreach (var result in results)
                {
                    var cached = saved.FirstOrDefault(game => game.IgdbId == result.IgdbId ||
                        (result.SteamAppId.HasValue && game.SteamAppId == result.SteamAppId));
                    result.Id = cached?.Id ?? 0;
                    result.MainHours = cached?.MainHours;
                }
                var gamesOnPage = results.Take(12).OrderByDescending(game => game.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
                return new CatalogPage(gamesOnPage, query, genre, page, results.Count > 12, true);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Live game discovery failed. HTTP status {StatusCode}.", exception.StatusCode);
                notice = "Live search is temporarily unavailable. Only saved games can be searched right now. Please try again later.";
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                notice = "The live catalogue took too long to respond. Showing saved games instead.";
            }
        }
        else
        {
            notice = "Live search is not connected. Only saved games are available right now.";
        }

        var local = database.Games.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matches = searchIndex.Search(query, (page - 1) * 12, 12, genre);
            var found = await local.Where(game => matches.Ids.Contains(game.Id)).ToListAsync(cancellationToken);
            var ordered = found.OrderBy(game => Array.IndexOf(matches.Ids, game.Id)).ToList();
            return new CatalogPage(ordered, query, genre, page, (long)page * 12 < matches.Total, false, notice);
        }
        if (!string.IsNullOrWhiteSpace(genre))
            local = local.Where(game => game.Genres.ToLower().Contains(genre.ToLower()));
        var games = await local.OrderBy(game => game.Name).Skip((page - 1) * 12).Take(13).ToListAsync(cancellationToken);
        return new CatalogPage(games.Take(12).ToList(), query, genre, page, games.Count > 12, false, notice);
    }

    public async Task<Game?> ImportAsync(int igdbId, CancellationToken cancellationToken, bool refresh = false)
    {
        var existing = await database.Games.FirstOrDefaultAsync(game => game.IgdbId == igdbId, cancellationToken);
        if (!igdb.IsConfigured || (!refresh && existing?.DataSource == "IGDB"))
            return existing;
        var imported = await igdb.GetAsync(igdbId, cancellationToken);
        if (imported is null)
            return existing;
        existing ??= await database.Games.FirstOrDefaultAsync(game => imported.SteamAppId.HasValue &&
            game.SteamAppId == imported.SteamAppId, cancellationToken);
        if (existing is not null)
        {
            imported.Id = existing.Id;
            database.Entry(existing).CurrentValues.SetValues(imported);
            await database.SaveChangesAsync(cancellationToken);
            searchIndex.Update(existing);
            await outputCache.EvictByTagAsync(CacheTag, CancellationToken.None);
            return existing;
        }

        database.Games.Add(imported);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            searchIndex.Update(imported);
            await outputCache.EvictByTagAsync(CacheTag, CancellationToken.None);
            return imported;
        }
        catch (DbUpdateException)
        {
            database.Entry(imported).State = EntityState.Detached;
            existing = await database.Games.FirstOrDefaultAsync(game => game.IgdbId == igdbId, cancellationToken);
            if (existing is null)
                throw;
            return existing;
        }
    }
}
