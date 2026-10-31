using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gamertab.Tests;

public class ApiTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SetCsrfTokenAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    private async Task<HttpClient> RegisterAsync()
    {
        var client = CreateClient();
        await SetCsrfTokenAsync(client);
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var response = await client.PostAsJsonAsync("/api/account/register", new { email, password = "GamertabTest2026", confirmPassword = "GamertabTest2026" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await SetCsrfTokenAsync(client);
        return client;
    }

    private static async Task<int> AddGameAsync(HttpClient client, int gameId = 1)
    {
        var response = await client.PostAsJsonAsync("/api/library", new { gameId, status = "Planned" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>();
        return entry.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task CataloguePageUsesGamertabBrand()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Gamertab", html);
    }

    [Fact]
    public async Task PaginationReturnsTheNextDistinctPage()
    {
        using var client = CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/games?skip=0&limit=2");
        var second = await client.GetFromJsonAsync<JsonElement>(first.GetProperty("nextLink").GetString());
        var firstIds = first.GetProperty("items").EnumerateArray().Select(game => game.GetProperty("id").GetInt32()).ToArray();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(game => game.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(2, firstIds.Length);
        Assert.Equal(2, secondIds.Length);
        Assert.Empty(firstIds.Intersect(secondIds));
        var last = await client.GetFromJsonAsync<JsonElement>("/api/games?skip=100&limit=2");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextLink").ValueKind);
    }

    [Theory]
    [InlineData("skip=-1&limit=2")]
    [InlineData("skip=0&limit=0")]
    [InlineData("skip=0&limit=101")]
    public async Task InvalidPaginationIsRejected(string query)
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/games?" + query)).StatusCode);
    }

    [Fact]
    public async Task PrivateApiRequiresAuthentication()
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/collections")).StatusCode);
    }

    [Fact]
    public async Task LibrarySupportsCreateUpdateReadAndDelete()
    {
        using var client = await RegisterAsync();
        var id = await AddGameAsync(client);
        var update = await client.PutAsJsonAsync($"/api/library/{id}", new
        {
            gameId = 1, status = "Completed", personalRating = 8.5, notes = "A great puzzle game.", startedOn = "2026-10-01", completedOn = "2026-10-20", followNews = true
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/library/{id}");
        Assert.Equal("Completed", saved.GetProperty("status").GetString());
        Assert.Equal(8.5, saved.GetProperty("personalRating").GetDouble());
        Assert.Equal("2026-10-20", saved.GetProperty("completedOn").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/library/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/library/{id}")).StatusCode);
    }

    [Fact]
    public async Task DuplicateGameIsRejected()
    {
        using var client = await RegisterAsync();
        await AddGameAsync(client);
        var duplicate = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Playing" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var entries = await client.GetFromJsonAsync<JsonElement>("/api/library");
        Assert.Equal(1, entries.GetArrayLength());
    }

    [Fact]
    public async Task InvalidDatesAndRatingDoNotCreateAnEntry()
    {
        using var client = await RegisterAsync();
        var wrongDates = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Completed", startedOn = "2026-10-20", completedOn = "2026-10-01" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongDates.StatusCode);
        var wrongRating = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Planned", personalRating = 11 });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRating.StatusCode);
        var wrongStatus = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Playing", completedOn = "2026-10-20" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongStatus.StatusCode);
        var entries = await client.GetFromJsonAsync<JsonElement>("/api/library");
        Assert.Equal(0, entries.GetArrayLength());
    }

    [Fact]
    public async Task AnotherUserCannotReadUpdateOrDeleteLibraryEntries()
    {
        using var owner = await RegisterAsync();
        var id = await AddGameAsync(owner);
        using var other = await RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/library/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/library/{id}", new { gameId = 1, status = "Dropped" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/library/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/library/{id}")).StatusCode);
    }

    [Fact]
    public async Task MissingCsrfTokenPreventsChanges()
    {
        using var client = await RegisterAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var response = await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Planned" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entries = await client.GetFromJsonAsync<JsonElement>("/api/library");
        Assert.Equal(0, entries.GetArrayLength());
    }

    [Fact]
    public async Task CollectionsSupportMembershipAndKeepLibraryDataOnDeletion()
    {
        using var client = await RegisterAsync();
        var entryId = await AddGameAsync(client);
        var created = await client.PostAsJsonAsync("/api/collections", new { name = "Weekend", description = "Puzzle games" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/collections/{id}/games", new { gameId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/collections/{id}/games", new { gameId = 1 })).StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
        Assert.Equal(1, saved.GetProperty("games").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/collections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/library/{entryId}")).StatusCode);
    }

    [Fact]
    public async Task AnotherUserCannotChangeACollection()
    {
        using var owner = await RegisterAsync();
        var created = await owner.PostAsJsonAsync("/api/collections", new { name = "Private collection" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var other = await RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/collections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/collections/{id}/games", new { gameId = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/collections/{id}", new { name = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/collections/{id}")).StatusCode);
    }

    [Fact]
    public async Task UnsafeCollectionImageUrlIsRejected()
    {
        using var client = await RegisterAsync();
        var response = await client.PostAsJsonAsync("/api/collections", new { name = "Test", coverUrl = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
