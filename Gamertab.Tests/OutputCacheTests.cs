using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Data;
using Gamertab.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class OutputCacheTests
{
    [Fact]
    public async Task CatalogueCachesResultsAndSeparatesSearchAndPagination()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        const string url = "/api/games?skip=0&limit=1&q=Portal";
        var original = await client.GetStringAsync(url);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Games.Where(game => game.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(game => game.Name, "Portal 2 revised"));

        Assert.Equal(original, await client.GetStringAsync(url));
        var larger = await client.GetFromJsonAsync<JsonElement>("/api/games?skip=0&limit=2&q=Portal");
        Assert.Equal("Portal 2 revised", larger.GetProperty("items")[0].GetProperty("name").GetString());
        var next = await client.GetFromJsonAsync<JsonElement>("/api/games?skip=1&limit=1&q=Portal");
        Assert.Equal(0, next.GetProperty("items").GetArrayLength());
        var otherSearch = await client.GetFromJsonAsync<JsonElement>("/api/games?skip=0&limit=1&q=Hades");
        Assert.Equal("Hades", otherSearch.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GameDetailsAreCachedSeparatelyForEachGame()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        var original = await client.GetStringAsync("/api/games/1");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Games.Where(game => game.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(game => game.Name, "Revised title"));

        Assert.Equal(original, await client.GetStringAsync("/api/games/1"));
        var other = await client.GetFromJsonAsync<JsonElement>("/api/games/2");
        Assert.Equal(2, other.GetProperty("id").GetInt32());
        Assert.Equal("Cyberpunk 2077", other.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ImportInvalidatesTheCatalogueAndDoesNotPreserveMissingGames()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/games");
        Assert.Equal(6, before.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/games/7")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Games/Open/1001")).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/games");
        Assert.Equal(7, after.GetProperty("total").GetInt32());
        var imported = await client.GetFromJsonAsync<JsonElement>("/api/games/7");
        Assert.Equal("Remote Game 1001", imported.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RefreshInvalidatesBothDetailsAndCatalogue()
    {
        using var factory = new CatalogFactory();
        using var client = factory.Browser();
        await client.GetStringAsync("/api/games/1");
        await client.GetStringAsync("/api/games?q=Portal");

        await using var scope = factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<GameCatalog>();
        await catalog.ImportAsync(72, CancellationToken.None, refresh: true);

        var updated = await client.GetFromJsonAsync<JsonElement>("/api/games/1");
        Assert.Equal("Remote Game 72", updated.GetProperty("name").GetString());
        var oldSearch = await client.GetFromJsonAsync<JsonElement>("/api/games?q=Portal");
        Assert.Equal(0, oldSearch.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SignedInRequestsBypassTheSharedCatalogueCache()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateClient();
        var original = await anonymous.GetStringAsync("/api/games/1");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Games.Where(game => game.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(game => game.Name, "Updated title"));

        using var signedIn = factory.CreateClient();
        var session = await signedIn.GetFromJsonAsync<JsonElement>("/api/account/session");
        signedIn.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var registration = await signedIn.PostAsJsonAsync("/api/account/register", new
        {
            email = "cache-test@example.test", password = "GamertabTest2026", confirmPassword = "GamertabTest2026"
        });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var current = await signedIn.GetFromJsonAsync<JsonElement>("/api/games/1");
        Assert.Equal("Updated title", current.GetProperty("name").GetString());
        Assert.Equal(original, await anonymous.GetStringAsync("/api/games/1"));
    }
}
