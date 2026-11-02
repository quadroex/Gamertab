using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gamertab.Tests;

public class CollectionSearchTests
{
    private static async Task<HttpClient> RegisterAsync(AppFactory factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await SetCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/account/register", new
        {
            email = Guid.NewGuid().ToString("N") + "@example.test", password = "GamertabTest2026", confirmPassword = "GamertabTest2026"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await SetCsrfAsync(client);
        return client;
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    private static async Task<int> CreateCollectionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collections", new { name = "Search verification" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task ShortQueriesDoNotReachIgdbAndSearchDoesNotImportResults()
    {
        using var factory = new CatalogFactory();
        using var client = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(client);
        var shortQuery = await client.GetFromJsonAsync<JsonElement>($"/Collections/GameOptions/{id}?term=Re");
        Assert.Equal(0, shortQuery.GetProperty("results").GetArrayLength());
        Assert.Empty(factory.Backchannel.Queries);

        var first = await client.GetFromJsonAsync<JsonElement>($"/Collections/GameOptions/{id}?term=Remote");
        Assert.Equal(12, first.GetProperty("results").GetArrayLength());
        Assert.True(first.GetProperty("pagination").GetProperty("more").GetBoolean());
        Assert.Equal("igdb:1001", first.GetProperty("results")[0].GetProperty("id").GetString());
        var second = await client.GetFromJsonAsync<JsonElement>($"/Collections/GameOptions/{id}?term=Remote&page=2");
        Assert.Equal("igdb:1013", second.GetProperty("results")[0].GetProperty("id").GetString());
        Assert.False(second.GetProperty("pagination").GetProperty("more").GetBoolean());
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/games");
        Assert.Equal(6, saved.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SelectingARemoteGameImportsItOnceAndAddsItToTheCollection()
    {
        using var factory = new CatalogFactory();
        using var client = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(client);
        var form = new Dictionary<string, string> { ["gameKey"] = "igdb:1001" };
        var added = await client.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        var collection = await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
        Assert.Equal("Remote Game 1001", collection.GetProperty("games")[0].GetProperty("name").GetString());

        await client.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(form));
        collection = await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
        Assert.Single(collection.GetProperty("games").EnumerateArray());
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/games");
        Assert.Equal(7, saved.GetProperty("total").GetInt32());
        var options = await client.GetFromJsonAsync<JsonElement>($"/Collections/GameOptions/{id}?term=Remote");
        Assert.DoesNotContain(options.GetProperty("results").EnumerateArray(), option => option.GetProperty("text").GetString() == "Remote Game 1001");
    }

    [Fact]
    public async Task LocalSearchWorksWithoutIgdbAndExcludesSelectedGames()
    {
        using var factory = new AppFactory();
        using var client = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(client);
        var response = await client.GetAsync($"/Collections/GameOptions/{id}?term=Portal");
        Assert.True(response.Headers.CacheControl?.NoStore);
        var options = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("saved:1", options.GetProperty("results")[0].GetProperty("id").GetString());
        Assert.Contains("saved catalogue", options.GetProperty("notice").GetString());
        await client.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["gameKey"] = "saved:1" }));
        options = await client.GetFromJsonAsync<JsonElement>($"/Collections/GameOptions/{id}?term=Portal");
        Assert.Equal(0, options.GetProperty("results").GetArrayLength());
        Assert.Contains("collection-games.js", await client.GetStringAsync($"/Collections/Details/{id}"));
    }

    [Fact]
    public async Task AnotherUserCannotSearchOrImportIntoTheCollection()
    {
        using var factory = new CatalogFactory();
        using var owner = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(owner);
        using var other = await RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Collections/GameOptions/{id}?term=Remote")).StatusCode);
        var response = await other.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["gameKey"] = "igdb:1001" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Backchannel.Queries);
    }

    [Fact]
    public async Task FailedImportLeavesCollectionUnchangedAndShowsAMessage()
    {
        using var factory = new CatalogFactory();
        factory.Backchannel.Fail = true;
        using var client = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(client);
        var response = await client.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["gameKey"] = "igdb:1001" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("temporarily unavailable", html);
        var collection = await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
        Assert.Equal(0, collection.GetProperty("games").GetArrayLength());
    }

    [Theory]
    [InlineData("unknown:1")]
    [InlineData("saved:-1")]
    [InlineData("igdb:2147483648")]
    [InlineData("")]
    public async Task InvalidSelectionsAreRejectedBeforeImport(string gameKey)
    {
        using var factory = new CatalogFactory();
        using var client = await RegisterAsync(factory);
        var id = await CreateCollectionAsync(client);
        var response = await client.PostAsync($"/Collections/AddGame/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["gameKey"] = gameKey }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Backchannel.Queries);
    }
}
