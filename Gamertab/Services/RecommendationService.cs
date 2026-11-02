using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class RecommendationService(AppDbContext database, IgdbClient igdb, ILogger<RecommendationService> logger)
{
    public async Task<List<Recommendation>> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var entries = await database.LibraryEntries.Include(entry => entry.Game).Where(entry => entry.UserId == userId).ToListAsync(cancellationToken);
        var positive = entries.Where(entry => entry.Status != PlayStatus.Dropped && (entry.PersonalRating is null || entry.PersonalRating >= 7))
            .OrderByDescending(entry => entry.PersonalRating ?? 0).ThenBy(entry => entry.GameId).ToList();
        var favourites = positive.Where(entry => entry.PersonalRating >= 7 || entry.Status is PlayStatus.Playing or PlayStatus.Completed)
            .Select(entry => entry.Game).Take(8).ToList();
        if (favourites.Count == 0)
            favourites = positive.Select(entry => entry.Game).Take(5).ToList();
        if (favourites.Count == 0)
            return [];

        var candidates = await database.Games.AsNoTracking().ToListAsync(cancellationToken);
        if (igdb.IsConfigured)
        {
            try
            {
                var remoteFavourites = await igdb.GetManyAsync(favourites.Where(game => game.IgdbId.HasValue).Select(game => game.IgdbId!.Value), cancellationToken);
                for (var index = 0; index < favourites.Count; index++)
                {
                    var fresh = remoteFavourites.FirstOrDefault(game => game.IgdbId == favourites[index].IgdbId);
                    if (fresh is not null)
                        favourites[index] = fresh;
                }
                var related = favourites.Select(game => game.SimilarIgdbIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0).ToArray()).ToList();
                var similarIds = Enumerable.Range(0, 10).SelectMany(index => related.Where(ids => ids.Length > index).Select(ids => ids[index])).Distinct().Take(40);
                var remoteCandidates = await igdb.GetManyAsync(similarIds, cancellationToken);
                try { remoteCandidates.AddRange(await igdb.FindRelatedAsync(favourites, cancellationToken)); }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning(exception, "Detailed recommendation search is unavailable. Using IGDB similar games.");
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Detailed recommendation search timed out. Using IGDB similar games.");
                }
                foreach (var candidate in remoteCandidates)
                {
                    var index = candidates.FindIndex(game => game.IgdbId == candidate.IgdbId ||
                        (candidate.SteamAppId.HasValue && game.SteamAppId == candidate.SteamAppId));
                    if (index < 0)
                        candidates.Add(candidate);
                    else
                    {
                        candidate.Id = candidates[index].Id;
                        candidates[index] = candidate;
                    }
                }
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Live recommendations are unavailable. Using saved game information.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        var ownedIds = entries.Select(entry => entry.GameId).ToHashSet();
        var ownedIgdbIds = entries.Select(entry => entry.Game.IgdbId).Where(id => id.HasValue).ToHashSet();
        var ownedSteamIds = entries.Select(entry => entry.Game.SteamAppId).Where(id => id.HasValue).ToHashSet();
        return candidates.Where(game => !ownedIds.Contains(game.Id) && (!game.IgdbId.HasValue || !ownedIgdbIds.Contains(game.IgdbId)) &&
                (!game.SteamAppId.HasValue || !ownedSteamIds.Contains(game.SteamAppId)))
            .Select(game => Score(game, favourites))
            .Where(item => item.Score >= 8)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Game.UserRating ?? 0)
            .ThenBy(item => item.Game.Name)
            .Take(12).ToList();
    }

    public static Recommendation Score(Game candidate, IReadOnlyList<Game> favourites)
    {
        var best = new Recommendation(candidate, 0, "");
        foreach (var favourite in favourites)
        {
            var genres = Split(candidate.Genres).Intersect(Split(favourite.Genres), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
            var themes = Split(candidate.Themes).Intersect(Split(favourite.Themes), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
            var similar = candidate.IgdbId.HasValue && Split(favourite.SimilarIgdbIds).Contains(candidate.IgdbId.Value.ToString());
            if (genres.Length == 0 && themes.Length == 0 && !similar)
                continue;
            var modes = Split(candidate.Modes).Intersect(Split(favourite.Modes), StringComparer.OrdinalIgnoreCase).Distinct().Count();
            var perspectives = Split(candidate.Perspectives).Intersect(Split(favourite.Perspectives), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
            var score = genres.Sum(genre => genre is "Adventure" or "Indie" ? 1 : 5) + themes.Sum(theme => theme == "Action" ? 1 : 3) + modes + (similar ? 8 : 0);
            if (perspectives.Length > 0)
                score += 10;
            else if (candidate.Perspectives.Length > 0 && favourite.Perspectives.Length > 0)
                score -= 6;
            var labels = string.Join(", ", genres.Where(genre => genre is not ("Adventure" or "Indie"))
                .Concat(perspectives).Concat(themes.Where(theme => theme != "Action")).Take(3));
            var reason = $"Because you like {favourite.Name}";
            reason += labels.Length > 0 ? ": " + labels : " · Similar game on IGDB";
            if (score > best.Score)
                best = new Recommendation(candidate, score, reason);
        }
        return best;
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
