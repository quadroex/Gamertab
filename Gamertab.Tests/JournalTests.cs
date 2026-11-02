using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gamertab.Tests;

public class JournalTests
{
    private static async Task<HttpClient> RegisterAsync(AppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await CsrfAsync(client);
        var email = $"journal-{Guid.NewGuid():N}@example.com";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/account/register", new { email, password = "GamertabTest42", confirmPassword = "GamertabTest42" })).StatusCode);
        await CsrfAsync(client);
        return client;
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    private static object Input(int revision, string text) => new { revision, blocks = new[] { new { type = "paragraph", text } }, clientId = Guid.NewGuid() };

    [Fact]
    public async Task JournalsRequireAuthenticationOwnershipCsrfAndValidBlocks()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/journals/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/journal/negotiate?negotiateVersion=1", null)).StatusCode);
        using var owner = await RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/journals/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/library", new { gameId = 1 })).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/journals/1", Input(0, "A"))).StatusCode);
        await CsrfAsync(owner);
        foreach (var body in new object[] {
            new { revision = -1, blocks = Array.Empty<object>() },
            new { revision = 0, blocks = new[] { new { type = "html", text = "<script>" } } },
            new { revision = 0, blocks = new[] { new { type = "paragraph", text = new string('a', 10001) } } },
            new { revision = 0, blocks = Enumerable.Repeat(new { type = "paragraph", text = "" }, 101) }
        }) Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/journals/1", body)).StatusCode);
        owner.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/hubs/journal/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task StaleRevisionsCannotOverwriteJournalsAndCopiesStayPrivate()
    {
        using var factory = new AppFactory();
        using var owner = await RegisterAsync(factory);
        using var other = await RegisterAsync(factory);
        await owner.PostAsJsonAsync("/api/library", new { gameId = 1 });
        await other.PostAsJsonAsync("/api/library", new { gameId = 1 });
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/journals/1", Input(0, "First"))).StatusCode);
        var stale = await owner.PutAsJsonAsync("/api/journals/1", Input(0, "Stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("First", await stale.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/journals/1", Input(1, "Second"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/journals/1", Input(1, "Stale again"))).StatusCode);
        var own = await owner.GetAsync("/api/journals/1");
        Assert.True(own.Headers.CacheControl?.NoStore);
        var snapshot = await own.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, snapshot.GetProperty("revision").GetInt32());
        Assert.Equal("Second", snapshot.GetProperty("blocks")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("Second", await other.GetStringAsync("/api/journals/1"));
        Assert.Equal(HttpStatusCode.OK, (await other.PutAsJsonAsync("/api/journals/1", Input(0, "Other account"))).StatusCode);
        Assert.DoesNotContain("Other account", await owner.GetStringAsync("/api/journals/1"));
        var entries = await owner.GetFromJsonAsync<JsonElement>("/api/library");
        await owner.DeleteAsync("/api/library/" + entries[0].GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/journals/1")).StatusCode);
        await owner.PostAsJsonAsync("/api/library", new { gameId = 1 });
        Assert.Contains("Second", await owner.GetStringAsync("/api/journals/1"));
    }
}
