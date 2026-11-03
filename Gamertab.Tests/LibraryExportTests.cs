using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gamertab.LibraryExport;
using Microsoft.AspNetCore.Http;

namespace Gamertab.Tests;

public class LibraryExportTests
{
    [Fact]
    public async Task FunctionFormatsMarkdownEscapesGameTitlesAndRejectsInvalidRequests()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new[] { new ExportGame(42, "Game [title]\n<script>", "Completed", 0, null, new DateOnly(2026, 1, 2)) }));
        context.Response.Body = new MemoryStream();
        await new Function().HandleAsync(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var markdown = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("## Completed", markdown);
        Assert.Contains("Rating 0/10", markdown);
        Assert.Contains("Finished 2026-01-02", markdown);
        Assert.Contains(@"Game \[title\] \<script\>", markdown);
        Assert.Contains("/Games/Details/42", markdown);
        foreach (var body in new[] { "{bad json", "null", "[{\"gameId\":-1,\"name\":\"Bad\",\"status\":\"Completed\"}]", "[{\"gameId\":1,\"name\":\"Bad\",\"status\":\"Other\"}]" })
        {
            var invalid = new DefaultHttpContext();
            invalid.Request.Method = "POST";
            invalid.Request.ContentType = "application/json";
            invalid.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            await new Function().HandleAsync(invalid);
            Assert.Equal(400, invalid.Response.StatusCode);
        }
        var get = new DefaultHttpContext();
        get.Request.Method = "GET";
        await new Function().HandleAsync(get);
        Assert.Equal(405, get.Response.StatusCode);
    }

    [Fact]
    public async Task LibraryExportRequiresSignInCsrfAndOnlyIncludesOwnedEntries()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Library/Export", null)).StatusCode);
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        }
        await RefreshToken();
        await client.PostAsJsonAsync("/api/account/register", new { email = "export@example.test", password = "GamertabTest42", confirmPassword = "GamertabTest42" });
        await RefreshToken();
        await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Completed", personalRating = 8 });
        var exported = await client.PostAsync("/Library/Export", null);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("gamertab-library.md", exported.Content.Headers.ContentDisposition!.FileName);
        var text = await exported.Content.ReadAsStringAsync();
        Assert.Contains("Portal 2", text);
        Assert.Contains("Rating 8/10", text);
        Assert.DoesNotContain("Cyberpunk", text);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Library/Export", null)).StatusCode);
    }
}
