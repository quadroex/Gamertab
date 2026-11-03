using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gamertab.Tests;

public class ActivityTests
{
    private static async Task<HttpClient> RegisterAsync(AppFactory factory)
    {
        var client = factory.CreateClient();
        async Task Token()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        }
        await Token();
        await client.PostAsJsonAsync("/api/account/register", new { email = $"activity-{Guid.NewGuid():N}@example.test", password = "GamertabTest42", confirmPassword = "GamertabTest42" });
        await Token();
        return client;
    }

    [Fact]
    public async Task ActivityRecordsCommittedChangesAndDoesNotExposeOtherAccounts()
    {
        using var factory = new AppFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/activity")).StatusCode);
        using var owner = await RegisterAsync(factory);
        using var other = await RegisterAsync(factory);
        var created = await owner.PostAsJsonAsync("/api/library", new { gameId = 1, notes = "Private note must not be included" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = entry.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/library", new { gameId = 1 })).StatusCode);
        await owner.PutAsJsonAsync($"/api/library/{id}", new { gameId = 1, status = "Completed" });
        await owner.DeleteAsync($"/api/library/{id}");
        var response = await owner.GetAsync("/api/activity");
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private note", text);
        var batch = JsonDocument.Parse(text).RootElement;
        var events = batch.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(new[] { "added", "updated", "removed" }, events.Select(item => item.GetProperty("action").GetString()));
        Assert.All(events, item => Assert.Equal("Portal 2", item.GetProperty("gameName").GetString()));
        var cursor = batch.GetProperty("cursor").GetInt64();
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/activity?after={cursor}")).GetProperty("events").EnumerateArray());
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/activity")).GetProperty("events").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/activity?after=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/activity?wait=21")).StatusCode);
    }

    [Fact]
    public async Task LongPollWakesOnAChangeAndSignalSubscriptionsAreIsolated()
    {
        var signal = new Gamertab.Services.ActivitySignal();
        using var first = signal.Watch("owner");
        using var second = signal.Watch("other");
        signal.Publish("owner");
        Assert.True(first.Changed.IsCompleted);
        Assert.False(second.Changed.IsCompleted);
        using var next = signal.Watch("owner");
        Assert.False(next.Changed.IsCompleted);
        using var factory = new AppFactory();
        using var owner = await RegisterAsync(factory);
        var pending = owner.GetAsync("/api/activity?wait=20");
        await owner.PostAsJsonAsync("/api/library", new { gameId = 1 });
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(batch.GetProperty("events").EnumerateArray());
    }
}
