using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class CatalogFactory : AppFactory
{
    protected override bool IgdbEnabled => true;
    public IgdbBackchannel Backchannel { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddHttpClient<IgdbClient>()
            .ConfigurePrimaryHttpMessageHandler(() => Backchannel));
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

public class IgdbBackchannel : HttpMessageHandler
{
    public bool Fail { get; set; }
    public List<string> Queries { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "id.twitch.tv")
            return Json(new { access_token = "igdb-test-token", expires_in = 3600 });
        var query = await request.Content!.ReadAsStringAsync(cancellationToken);
        Queries.Add(query);
        if (Fail)
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (request.RequestUri.AbsolutePath.EndsWith("game_time_to_beats"))
            return Json(new[] { new { hastily = 7200, normally = 10800, completely = 18000, count = 5 } });
        var match = Regex.Match(query, @"where id = \((\d+)\)");
        if (match.Success)
            return Json(new[] { Game(int.Parse(match.Groups[1].Value)) });
        if (query.Contains("No matches"))
            return Json(Array.Empty<object>());
        if (query.Contains("Portal"))
            return Json(new[] { Game(72, "Portal 2") });
        if (query.Contains("offset 12;"))
            return Json(new[] { Game(1013), Game(1014) });
        return Json(Enumerable.Range(1001, 13).Select(id => Game(id)).ToArray());
    }

    private static object Game(int id, string? name = null) => new
    {
        id, name = name ?? $"Remote Game {id}", summary = "A game from the live catalogue.",
        genres = new[] { new { name = "Puzzle" } }, platforms = new[] { new { name = "PC (Microsoft Windows)" } }
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

public class CatalogTests
{
    [Fact]
    public async Task HomepageShowsRemoteGamesAndOffersAnotherPage()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/");
        Assert.Contains("Remote Game 1001", html);
        Assert.Contains("Remote Game 1012", html);
        Assert.DoesNotContain("Remote Game 1013", html);
        Assert.Contains("Live discovery", html);
        Assert.Contains("/Games/Open/1001", html);
        Assert.Contains("page=2", html);
        Assert.Contains("sort rating_count desc;", factory.Backchannel.Queries.Single());
        Assert.Contains("game_type = (0,4,8,9,10,11)", factory.Backchannel.Queries.Single());
        Assert.Contains("version_parent = null", factory.Backchannel.Queries.Single());
    }

    [Fact]
    public async Task SearchAndGenreArePassedToIgdbWithPagination()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/?q=Remote&genre=Puzzle&page=2");
        Assert.Contains("Remote Game 1013", html);
        Assert.DoesNotContain("Remote Game 1012", html);
        Assert.DoesNotContain(">Next</a>", html);
        var query = factory.Backchannel.Queries.Single();
        Assert.Contains("search \"Remote\";", query);
        Assert.Contains("genres = (9)", query);
        Assert.Contains("offset 12;", query);
        Assert.Contains("limit 13;", query);
    }

    [Fact]
    public async Task ExistingGamesUseTheirSavedDetailsPage()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/?q=Portal");
        Assert.Contains("/Games/Details/1", html);
        Assert.DoesNotContain("/Games/Open/72", html);
        Assert.DoesNotContain("Import &amp; view", html);
    }

    [Fact]
    public async Task AnonymousVisitorCanOpenAndCacheRemoteDetailsWithoutDuplicates()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var response = await client.GetAsync("/Games/Open/1001");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Games/Details/7", response.Headers.Location?.OriginalString);
        var game = await client.GetFromJsonAsync<JsonElement>("/api/games/7");
        Assert.Equal("Remote Game 1001", game.GetProperty("name").GetString());
        Assert.Equal(2, game.GetProperty("mainHours").GetDouble());
        await client.GetAsync("/Games/Open/1001");
        var catalogue = await client.GetFromJsonAsync<JsonElement>("/api/games");
        Assert.Equal(7, catalogue.GetProperty("total").GetInt32());
        Assert.Equal(2, factory.Backchannel.Queries.Count);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/session")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task ServiceFailureShowsAnErrorWithoutSubstitutingSavedGames()
    {
        using var factory = new CatalogFactory();
        factory.Backchannel.Fail = true;
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/");
        Assert.Contains("temporarily unavailable", html);
        Assert.DoesNotContain("Portal 2", html);
        Assert.Contains("Search unavailable", html);
        Assert.Contains("Try again", html);
        Assert.Contains("saved=true", html);
    }

    [Fact]
    public async Task MissingCredentialsDoNotTurnLiveSearchIntoASavedSearch()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/?q=Portal");
        Assert.Contains("Live search is not connected", html);
        Assert.Contains("Search unavailable", html);
        Assert.DoesNotContain("/Games/Details/1", html);
    }

    [Fact]
    public async Task NonExactRemoteMatchesAreExplainedAndStillCanBeOpened()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/?q=Remote");
        Assert.Contains("No exact title match on this page", html);
        Assert.Contains("Remote Game 1001", html);
        Assert.Contains("/Games/Open/1001", html);
    }

    [Fact]
    public async Task EmptySearchDoesNotInventLocalMatches()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var html = await client.GetStringAsync("/?q=No%20matches");
        Assert.Contains("No games found", html);
        Assert.DoesNotContain("Portal 2", html);
    }

    [Theory]
    [InlineData("page=-1")]
    [InlineData("page=10001")]
    [InlineData("genre=Unknown")]
    public async Task InvalidFiltersDoNotReachTheRemoteService(string query)
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/?" + query)).StatusCode);
        Assert.Empty(factory.Backchannel.Queries);
    }
}
