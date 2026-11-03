using Gamertab.Models;
using Gamertab.Services;
using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Data;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public void ASharedGameModeAloneDoesNotCreateARecommendation()
    {
        var favourite = new Game { Name = "Puzzle favourite", Genres = "Puzzle", Themes = "Science fiction", Modes = "Single player" };
        var candidate = new Game { Genres = "Sport", Themes = "Business", Modes = "Single player" };
        Assert.Equal(0, RecommendationService.Score(candidate, [favourite]).Score);
    }

    [Fact]
    public void IgdbSimilarityAddsAReasonEvenWithoutMatchingTags()
    {
        var favourite = new Game { Name = "My favourite", SimilarIgdbIds = "321,654" };
        var candidate = new Game { IgdbId = 654, Name = "Related game" };
        var result = RecommendationService.Score(candidate, [favourite]);
        Assert.True(result.Score > 0);
        Assert.Contains("My favourite", result.Reason);
        Assert.Contains("IGDB", result.Reason);
    }

    [Fact]
    public void MatchingPerspectiveRanksAboveBroadTagsFromADifferentKindOfGame()
    {
        var favourite = new Game { Name = "Puzzle favourite", Genres = "Puzzle,Platform,Adventure", Themes = "Science fiction,Action", Perspectives = "First person", SimilarIgdbIds = "10,20" };
        var puzzle = new Game { IgdbId = 10, Genres = "Puzzle,Adventure", Themes = "Science fiction", Perspectives = "First person" };
        var platformer = new Game { IgdbId = 20, Genres = "Puzzle,Platform,Adventure", Themes = "Science fiction,Action", Perspectives = "Third person" };
        Assert.True(RecommendationService.Score(puzzle, [favourite]).Score > RecommendationService.Score(platformer, [favourite]).Score);
    }

    [Fact]
    public async Task PersonalPicksStayOnForYouAndDoNotUseLowRatingsAsFavourites()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        (await client.PostAsJsonAsync("/api/account/register", new { email = "picks@example.test", password = "GamertabTest2026", confirmPassword = "GamertabTest2026" })).EnsureSuccessStatusCode();
        session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.Games.Add(new Game { Name = "Puzzle neighbour", Genres = "Puzzle", Themes = "Science fiction", Modes = "Single player" });
            await database.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Playing", personalRating = 9 });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var html = await client.GetStringAsync("/");
        Assert.Contains("id=\"catalogue\"", html);
        Assert.DoesNotContain("Recommended for you", html);
        Assert.DoesNotContain("Because you like", html);
        var picks = await client.GetStringAsync("/Library/Recommendations");
        Assert.Contains("Because you like Portal 2", picks);
        Assert.DoesNotContain("/Games/Details/1\"", picks);
        (await client.PutAsJsonAsync($"/api/library/{id}", new { gameId = 1, status = "Playing", personalRating = 2 })).EnsureSuccessStatusCode();
        var afterLowRating = await client.GetStringAsync("/Library/Recommendations");
        Assert.DoesNotContain("Because you like Portal 2", afterLowRating);
        Assert.Contains("Give us a starting point", afterLowRating);
    }
}
