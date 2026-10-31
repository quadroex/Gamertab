using Gamertab.Models;
using Gamertab.Services;

namespace Gamertab.Tests;

public class RecommendationTests
{
    [Fact]
    public void MatchingGenresAndThemesRankAboveOnlyMatchingMode()
    {
        var favourites = new[] { new Game { Genres = "Puzzle,Adventure", Themes = "Science fiction", Modes = "Single player" } };
        var matching = RecommendationService.Score(new Game { Genres = "Puzzle", Themes = "Science fiction", Modes = "Single player" }, favourites);
        var unrelated = RecommendationService.Score(new Game { Genres = "Sport", Themes = "", Modes = "Single player" }, favourites);
        Assert.True(matching.Score > unrelated.Score);
        Assert.Contains("Puzzle", matching.Reason);
        Assert.Contains("Science fiction", matching.Reason);
    }

    [Fact]
    public void RepeatedFavouriteDoesNotArtificiallyIncreaseScore()
    {
        var favourite = new Game { Genres = "Puzzle", Themes = "Science fiction" };
        var candidate = new Game { Genres = "Puzzle", Themes = "Science fiction" };
        Assert.Equal(RecommendationService.Score(candidate, [favourite]).Score, RecommendationService.Score(candidate, [favourite, favourite]).Score);
    }
}
