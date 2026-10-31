using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class RecommendationService(AppDbContext database, IgdbClient igdb)
{
    public async Task<List<Recommendation>> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var entries = await database.LibraryEntries.Include(entry => entry.Game).Where(entry => entry.UserId == userId).ToListAsync(cancellationToken);
        var favourites = entries.Where(entry => entry.PersonalRating >= 7 || entry.Status == PlayStatus.Playing).Select(entry => entry.Game).ToList();
        if (favourites.Count == 0)
            favourites = entries.Where(entry => entry.Status != PlayStatus.Dropped).Select(entry => entry.Game).Take(5).ToList();
        if (favourites.Count == 0)
            return [];

        var candidates = await database.Games.AsNoTracking().ToListAsync(cancellationToken);
        if (igdb.IsConfigured)
        {
            try
            {
                var remoteFavourites = await igdb.GetManyAsync(favourites.Where(game => game.IgdbId.HasValue).Select(game => game.IgdbId!.Value), cancellationToken);
                var similarIds = remoteFavourites.SelectMany(game => game.SimilarIgdbIds.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(int.Parse).Distinct().Take(30);
                var remoteCandidates = await igdb.GetManyAsync(similarIds, cancellationToken);
                foreach (var candidate in remoteCandidates)
                    if (!candidates.Any(game => game.IgdbId == candidate.IgdbId))
                        candidates.Add(candidate);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        var ownedIds = entries.Select(entry => entry.GameId).ToHashSet();
        var ownedIgdbIds = entries.Select(entry => entry.Game.IgdbId).Where(id => id.HasValue).ToHashSet();
        return candidates.Where(game => !ownedIds.Contains(game.Id) && (!game.IgdbId.HasValue || !ownedIgdbIds.Contains(game.IgdbId)))
            .Select(game => Score(game, favourites))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Game.Name)
            .Take(12).ToList();
    }

    public static Recommendation Score(Game candidate, IReadOnlyList<Game> favourites)
    {
        var genres = Split(candidate.Genres).Intersect(favourites.SelectMany(game => Split(game.Genres)), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
        var themes = Split(candidate.Themes).Intersect(favourites.SelectMany(game => Split(game.Themes)), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
        var modes = Split(candidate.Modes).Intersect(favourites.SelectMany(game => Split(game.Modes)), StringComparer.OrdinalIgnoreCase).Distinct().ToArray();
        var score = genres.Length * 4 + themes.Length * 3 + modes.Length;
        var reasons = genres.Concat(themes).Take(3).ToArray();
        return new Recommendation(candidate, score, reasons.Length > 0 ? "Shared interests: " + string.Join(", ", reasons) : "Matches your preferred game modes");
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
