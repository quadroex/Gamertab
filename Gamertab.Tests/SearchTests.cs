using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Models;
using Gamertab.Services;

namespace Gamertab.Tests;

public class SearchTests
{
    [Theory]
    [InlineData("space dwarves", "Deep Rock Galactic")]
    [InlineData("FANTASY", "Hades")]
    [InlineData("cyberp", "Cyberpunk 2077")]
    public async Task ApiSearchesDescriptionsThemesAndNamePrefixes(string query, string expectedName)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>("/api/games?q=" + Uri.EscapeDataString(query));
        Assert.Equal(1, response.GetProperty("total").GetInt32());
        Assert.Equal(expectedName, response.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiSearchPreservesRelevanceAcrossPagesAndHandlesLargeOffsets()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/games?q=science%20fiction&limit=1");
        var second = await client.GetFromJsonAsync<JsonElement>(first.GetProperty("nextLink").GetString());
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(3, second.GetProperty("total").GetInt32());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetInt32(), second.GetProperty("items")[0].GetProperty("id").GetInt32());
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/games?q=science&skip=2147483647");
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
        Assert.Equal(3, empty.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextLink").ValueKind);
    }

    [Fact]
    public async Task SavedCatalogueUsesFullTextSearchAndKeepsGenreFilters()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var matches = await client.GetStringAsync("/?q=cooperative&genre=Puzzle");
        Assert.Contains("Portal 2", matches);
        Assert.DoesNotContain("Deep Rock Galactic", matches);
        var filtered = await client.GetStringAsync("/?q=cooperative&genre=Shooter");
        Assert.Contains("Deep Rock Galactic", filtered);
        Assert.DoesNotContain("Portal 2", filtered);
    }

    [Fact]
    public async Task ImportsBecomeSearchableImmediately()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/games?q=1001");
        Assert.Equal(0, before.GetProperty("total").GetInt32());
        await client.GetAsync("/Games/Open/1001");
        var after = await client.GetFromJsonAsync<JsonElement>("/api/games?q=1001");
        Assert.Equal(1, after.GetProperty("total").GetInt32());
        Assert.Equal("Remote Game 1001", after.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void NamesRankAboveDescriptionsAndAllQueryWordsAreRequired()
    {
        using var index = new GameSearchIndex();
        index.Rebuild([
            new Game { Id = 1, Name = "Puzzle Journey", Summary = "A quiet adventure" },
            new Game { Id = 2, Name = "Another Journey", Summary = "A puzzle adventure" }
        ]);
        Assert.Equal(new[] { 1, 2 }, index.Search("puzzle", 0, 10).Ids);
        Assert.Equal(new[] { 2 }, index.Search("puzzle another", 0, 10).Ids);
        Assert.Empty(index.Search("puzzle absent", 0, 10).Ids);
    }

    [Fact]
    public void UpdatesAndRebuildsRemoveOutdatedTermsWithoutDuplicatingGames()
    {
        using var index = new GameSearchIndex();
        index.Update(new Game { Id = 1, Name = "Original title" });
        index.Update(new Game { Id = 1, Name = "Renamed title" });
        Assert.Empty(index.Search("original", 0, 10).Ids);
        Assert.Equal(new[] { 1 }, index.Search("renamed", 0, 10).Ids);
        index.Rebuild([new Game { Id = 2, Name = "Another title" }]);
        Assert.Equal(new[] { 2 }, index.Search("title", 0, 10).Ids);
    }

    [Theory]
    [InlineData("\"*:+()[]")]
    [InlineData("   ")]
    public async Task EmptyAndPunctuationQueriesDoNotCauseParserErrors(string query)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>("/api/games?q=" + Uri.EscapeDataString(query));
        Assert.Equal(string.IsNullOrWhiteSpace(query) ? 6 : 0, response.GetProperty("total").GetInt32());
    }
}
