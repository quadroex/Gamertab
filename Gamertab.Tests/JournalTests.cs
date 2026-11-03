using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task BlockIdsDistinguishIdenticalNotesAndSurviveReorderingAndEditing()
    {
        using var factory = new AppFactory();
        using var owner = await RegisterAsync(factory);
        await owner.PostAsJsonAsync("/api/library", new { gameId = 1 });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var saved = await owner.PutAsJsonAsync("/api/journals/1", new
        {
            revision = 0,
            blocks = new[] { new { type = "paragraph", text = "Same note", id = first }, new { type = "paragraph", text = "Same note", id = second } }
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var acknowledged = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first, acknowledged.GetProperty("blocks")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/journals/1", new
        {
            revision = 1,
            blocks = new[] { new { type = "paragraph", text = "Edited note", id = second }, new { type = "paragraph", text = "Same note", id = first } }
        })).StatusCode);
        foreach (var ids in new[] { new Guid?[] { first, first }, new Guid?[] { Guid.Empty, second }, new Guid?[] { first, null } })
        {
            var invalid = await owner.PutAsJsonAsync("/api/journals/1", new
            {
                revision = 2,
                blocks = ids.Select(id => new { type = "paragraph", text = "Invalid", id })
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var snapshot = await owner.GetFromJsonAsync<JsonElement>("/api/journals/1");
        Assert.Equal(2, snapshot.GetProperty("revision").GetInt32());
        Assert.Equal(second, snapshot.GetProperty("blocks")[0].GetProperty("id").GetGuid());
        Assert.Equal(first, snapshot.GetProperty("blocks")[1].GetProperty("id").GetGuid());
        Assert.Equal("Edited note", snapshot.GetProperty("blocks")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task LegacyDocumentsAndIdlessClientsReceiveConsistentBlockIdsWithoutChangingOldNotes()
    {
        using var factory = new AppFactory();
        using var owner = await RegisterAsync(factory);
        await owner.PostAsJsonAsync("/api/library", new { gameId = 1 });
        var empty = await owner.GetFromJsonAsync<JsonElement>("/api/journals/1");
        var anotherRead = await owner.GetFromJsonAsync<JsonElement>("/api/journals/1");
        Assert.Equal(empty.GetProperty("blocks")[0].GetProperty("id").GetGuid(), anotherRead.GetProperty("blocks")[0].GetProperty("id").GetGuid());
        const string originalJson = """[{"Type":"paragraph","Text":"Old session notes"}]""";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.PlayJournals.Add(new PlayJournal
            {
                UserId = await database.Users.Select(user => user.Id).SingleAsync(), GameId = 1, Revision = 5, BlocksJson = originalJson
            });
            await database.SaveChangesAsync();
        }
        var legacy = await owner.GetFromJsonAsync<JsonElement>("/api/journals/1");
        var legacyId = legacy.GetProperty("blocks")[0].GetProperty("id").GetGuid();
        Assert.Equal(5, legacy.GetProperty("revision").GetInt32());
        Assert.Equal("Old session notes", legacy.GetProperty("blocks")[0].GetProperty("text").GetString());
        var nextRead = await owner.GetFromJsonAsync<JsonElement>("/api/journals/1");
        Assert.Equal(legacyId, nextRead.GetProperty("blocks")[0].GetProperty("id").GetGuid());
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Equal(originalJson, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PlayJournals.Select(journal => journal.BlocksJson).SingleAsync());
        var updated = await owner.PutAsJsonAsync("/api/journals/1", Input(5, "Updated by an older client"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var result = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(legacyId, result.GetProperty("blocks")[0].GetProperty("id").GetGuid());
        Assert.Equal(6, result.GetProperty("revision").GetInt32());
    }

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
