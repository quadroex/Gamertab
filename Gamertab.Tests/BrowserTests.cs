using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Gamertab.Tests;

public class BrowserFactory : PlanFactory
{
    protected override bool IgdbEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:BlobStorage"] = "UseDevelopmentStorage=true",
            ["ConnectionStrings:QueueStorage"] = "",
            ["Gemini:ApiKey"] = "browser-test-key"
        }));
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<GeminiAgent>().ConfigurePrimaryHttpMessageHandler(() => new AssistantBackchannel { Answer = "### Your library\n\n* **[Portal 2](/Games/Details/1)** - Puzzle\n* *Cyberpunk 2077* - RPG\n\n[Open Portal 2](/Games/Details/1)" });
            services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => new BrowserBackchannel());
            services.AddHttpClient<SteamNewsClient>().ConfigurePrimaryHttpMessageHandler(() => new BrowserBackchannel());
            services.AddHttpClient<SteamStoreClient>().ConfigurePrimaryHttpMessageHandler(() => new BrowserBackchannel());
        });
    }
}

public class BrowserBackchannel : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        object data;
        if (request.RequestUri!.Host == "id.twitch.tv")
            data = new { access_token = "browser-test-token", expires_in = 3600 };
        else if (request.RequestUri.Host == "api.steampowered.com")
            data = new { appnews = new { newsitems = Array.Empty<object>() } };
        else if (request.RequestUri.Host == "store.steampowered.com")
            data = new Dictionary<string, object>();
        else
        {
            var query = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (query.Contains("screenshots.image_id"))
                data = new[] { new { id = 1001, screenshots = new[] { new { image_id = "screen1" }, new { image_id = "screen2" } },
                    videos = new[] { new { name = "Launch trailer", video_id = "abcDEF12_-3" } } } };
            else if (request.RequestUri.AbsolutePath.EndsWith("game_time_to_beats"))
                data = Array.Empty<object>();
            else if (query.Contains("No matches"))
                data = Array.Empty<object>();
            else
                data = new[] { new { id = 1001, name = "Browser Quest", summary = "A game discovered outside the starter catalogue.",
                    genres = new[] { new { name = "Puzzle" }, new { name = "Adventure" } },
                    themes = new[] { new { name = "Science fiction" } },
                    platforms = new[] { new { name = "PC (Microsoft Windows)" } }, cover = new { image_id = "cover1" } } };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
    }
}

public class BrowserFixture : IAsyncLifetime
{
    private IPlaywright playwright = null!;
    public BrowserFactory Factory { get; } = new();
    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";
    public string ResultsPath { get; } = Path.Combine(Path.GetDirectoryName(typeof(BrowserFixture).Assembly.Location)!, "browser-results");

    public async Task InitializeAsync()
    {
        Factory.UseKestrel(0);
        using var client = Factory.CreateClient();
        BaseUrl = client.BaseAddress!.ToString();
        Directory.CreateDirectory(ResultsPath);
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = Environment.GetEnvironmentVariable("GAMERTAB_TEST_BROWSER") ?? "msedge",
            Headless = true
        });
    }

    public async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl, ViewportSize = new() { Width = 1365, Height = 900 }, ReducedMotion = ReducedMotion.Reduce });
        await ConfigureRoutesAsync(context);
        return context;
    }

    public async Task<IBrowserContext> OpenProfileAsync(string path)
    {
        var context = await playwright.Chromium.LaunchPersistentContextAsync(path, new()
        {
            Channel = Environment.GetEnvironmentVariable("GAMERTAB_TEST_BROWSER") ?? "msedge", Headless = true,
            BaseURL = BaseUrl, ViewportSize = new() { Width = 1365, Height = 900 }, ReducedMotion = ReducedMotion.Reduce
        });
        await ConfigureRoutesAsync(context);
        return context;
    }

    private async Task ConfigureRoutesAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
        {
            if (route.Request.Url.StartsWith(BaseUrl, StringComparison.Ordinal))
                await route.ContinueAsync();
            else if (route.Request.ResourceType == "image")
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4XcAAAAASUVORK5CYII=") });
            else
                await route.FulfillAsync(new() { ContentType = "text/html", Body = "<html><title>Test video</title></html>" });
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        playwright?.Dispose();
        await Factory.DisposeAsync();
    }
}

[Trait("Category", "Browser")]
public class BrowserTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    private const string Password = "GamertabTest42";

    private static async Task<string> RegisterAsync(IPage page)
    {
        var email = $"browser-{Guid.NewGuid():N}@example.com";
        await page.GotoAsync("/Account/Register");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("Confirm password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
        return email;
    }

    [Theory]
    [InlineData("signalr")]
    [InlineData("websocket")]
    [InlineData("longpoll")]
    [InlineData("adaptive")]
    public async Task LibraryActivityDeliversChangesThroughEachTransportAndRestoresHistory(string mode)
    {
        await using var first = await fixture.NewContextAsync();
        var page = await first.NewPageAsync();
        var email = await RegisterAsync(page);
        var ownerSession = (await (await first.APIRequest.GetAsync("/api/account/session")).JsonAsync())!.Value;
        var headers = new Dictionary<string, string> { ["X-CSRF-TOKEN"] = ownerSession.GetProperty("csrfToken").GetString()! };
        var created = await first.APIRequest.PostAsync("/api/library", new() { Headers = headers, DataObject = new { gameId = 1 } });
        Assert.Equal(201, created.Status);
        var entryId = (await created.JsonAsync())!.Value.GetProperty("id").GetInt32();
        await using var second = await fixture.NewContextAsync();
        var anonymousSession = (await (await second.APIRequest.GetAsync("/api/account/session")).JsonAsync())!.Value;
        var login = await second.APIRequest.PostAsync("/api/account/login", new()
        {
            Headers = new Dictionary<string, string> { ["X-CSRF-TOKEN"] = anonymousSession.GetProperty("csrfToken").GetString()! }, DataObject = new { email, password = Password }
        });
        Assert.Equal(200, login.Status);
        var remote = await second.NewPageAsync();
        var errors = new List<string>();
        remote.PageError += (_, error) => errors.Add(error);
        await remote.GotoAsync("/Library/Activity");
        await Expect(remote.Locator("[data-event-id]")).ToHaveCountAsync(1);
        await remote.GetByText("Connection details", new() { Exact = true }).ClickAsync();
        await remote.GetByLabel("Update method").SelectOptionAsync(mode);
        Assert.Equal(200, (await first.APIRequest.PutAsync($"/api/library/{entryId}", new() { Headers = headers, DataObject = new { gameId = 1, status = "Completed" } })).Status);
        await Expect(remote.GetByText("Library entry updated", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(remote.Locator("[data-event-id]")).ToHaveCountAsync(2);
        if (mode == "websocket")
        {
            await second.SetOfflineAsync(true);
            Assert.Equal(200, (await first.APIRequest.PutAsync($"/api/library/{entryId}", new() { Headers = headers, DataObject = new { gameId = 1, status = "Playing" } })).Status);
            await second.SetOfflineAsync(false);
            await Expect(remote.Locator("[data-event-id]")).ToHaveCountAsync(3, new() { Timeout = 15000 });
        }
        Assert.Equal(204, (await first.APIRequest.DeleteAsync($"/api/library/{entryId}", new() { Headers = headers })).Status);
        await Expect(remote.GetByText("Removed from your library", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });
        var count = await remote.Locator("[data-event-id]").CountAsync();
        await remote.ReloadAsync();
        await Expect(remote.Locator("[data-event-id]")).ToHaveCountAsync(count);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AssistantShowsToolResultsStreamsTextAndClearsConversation()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await RegisterAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "Assistant", Exact = true }).ClickAsync();
        await page.GetByLabel("Message", new() { Exact = true }).FillAsync("Read my library");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-status]")).ToHaveTextAsync("Done");
        await Expect(page.Locator(".assistant-text h3")).ToHaveTextAsync("Your library");
        await Expect(page.Locator(".assistant-text li")).ToHaveCountAsync(2);
        await Expect(page.Locator(".assistant-text li strong")).ToHaveTextAsync("Portal 2");
        await Expect(page.Locator(".assistant-text li strong a")).ToHaveAttributeAsync("href", "/Games/Details/1");
        await Expect(page.Locator(".assistant-text li em")).ToHaveTextAsync("Cyberpunk 2077");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open Portal 2" })).ToHaveAttributeAsync("href", "/Games/Details/1");
        Assert.DoesNotContain("*", await page.Locator(".assistant-text").Last.InnerTextAsync());
        await Expect(page.Locator(".assistant-message details")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-messages] script")).ToHaveCountAsync(0);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "assistant.png"), FullPage = true });
        await page.EvaluateAsync("formatChat(document.querySelector('.assistant-text'), '<img src=x onerror=alert(1)>\\n\\n```html\\n<script>alert(1)</script>\\n```\\n\\n[Unsafe](javascript:alert(1))')");
        await Expect(page.Locator(".assistant-text").First.Locator("img, script, a")).ToHaveCountAsync(0);
        await Expect(page.Locator(".assistant-text pre code")).ToContainTextAsync("<script>");
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForFunctionAsync("document.documentElement.scrollWidth <= window.innerWidth");
        await page.GetByRole(AriaRole.Button, new() { Name = "New conversation", Exact = true }).ClickAsync();
        await Expect(page.Locator(".assistant-message")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task BrowseShowsSearchAndPopularGamesWhilePersonalPicksStayOnForYou()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await RegisterAsync(page);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Browse", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Popular games", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Search catalogue")).ToBeVisibleAsync();
        await Expect(page.Locator(".hero, .recommendations-section")).ToHaveCountAsync(0);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "browse.png"), FullPage = true });
        await page.GetByRole(AriaRole.Link, new() { Name = "For you", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your next favourite?" })).ToBeVisibleAsync();
        await Expect(page.GetByText("Picked for you", new() { Exact = true }).First).ToBeVisibleAsync();
    }

    private static async Task OpenRemoteGameAsync(IPage page)
    {
        await page.GotoAsync("/");
        await page.GetByLabel("Search catalogue").FillAsync("Browser Quest");
        await page.GetByRole(AriaRole.Button, new() { Name = "Find games", Exact = true }).ClickAsync();
        await page.Locator("#catalogue").GetByRole(AriaRole.Link, new() { Name = "Browser Quest", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Browser Quest", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task RegistrationLoginValidationAndPrivatePagesWorkInTheBrowser()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/Library");
        await Expect(page).ToHaveURLAsync(new Regex("/Account/Login"));
        await page.GotoAsync("/Library/Statistics");
        await Expect(page).ToHaveURLAsync(new Regex("/Account/Login"));
        var email = await RegisterAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await page.GotoAsync("/Account/Login");
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("WrongPassword42");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(page.Locator(".validation-summary-errors")).ToContainTextAsync("Sign-in failed");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GotoAsync("/Library");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "My library", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task LiveSearchProgressValidationAndDeletionPersistAfterReload()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await RegisterAsync(page);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("2");
        await page.GetByLabel("Your rating out of 10").FillAsync("8.5");
        await page.GetByLabel("Started", new() { Exact = true }).FillAsync("2026-02-10");
        await page.GetByLabel("Completed", new() { Exact = true }).FillAsync("2026-02-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await Expect(page.Locator(".validation-summary-errors")).ToContainTextAsync("date");
        await page.GetByLabel("Completed", new() { Exact = true }).FillAsync("2026-02-20");
        await page.GetByLabel("Your notes").FillAsync("Finished the main story");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator(".library-row")).ToHaveCountAsync(1);
        await Expect(page.Locator(".library-row")).ToContainTextAsync("8.5");
        await Expect(page.Locator(".library-row")).ToContainTextAsync("Finished the main story");
        await page.GetByRole(AriaRole.Link, new() { Name = "Playing", Exact = true }).ClickAsync();
        await Expect(page.Locator(".library-row")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Link, new() { Name = "Completed", Exact = true }).ClickAsync();
        await Expect(page.Locator(".library-row")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator(".library-row")).ToHaveCountAsync(0);
        await page.GotoAsync("/?q=No%20matches");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No games found", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CollectionPickerEditsAndAccountIsolationWorkWithoutRemovingLibraryGames()
    {
        await using var owner = await fixture.NewContextAsync();
        var page = await owner.NewPageAsync();
        await RegisterAsync(page);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        var entryUrl = page.Url;
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await page.GotoAsync("/Collections/Create");
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Weekend picks");
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync("Games to try next");
        await page.GetByLabel("No cover", new() { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save collection", Exact = true }).ClickAsync();
        var collectionUrl = page.Url;
        await page.Locator(".select2-selection").ClickAsync();
        await page.Locator(".select2-search__field").FillAsync("Br");
        await Expect(page.Locator(".select2-results")).ToContainTextAsync("1 or more");
        await page.Locator(".select2-search__field").FillAsync("Browser Quest");
        await page.GetByRole(AriaRole.Option, new() { Name = "Browser Quest", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add game +", Exact = true }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator(".game-grid")).ToContainTextAsync("Browser Quest");
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit collection", Exact = true }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Finished favourites");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save collection", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Finished favourites", Exact = true })).ToBeVisibleAsync();

        await using var visitor = await fixture.NewContextAsync();
        var otherPage = await visitor.NewPageAsync();
        await RegisterAsync(otherPage);
        var collectionResponse = await visitor.APIRequest.GetAsync(collectionUrl);
        Assert.Equal(404, collectionResponse!.Status);
        var entryResponse = await visitor.APIRequest.GetAsync(entryUrl);
        Assert.Equal(404, entryResponse!.Status);
        await otherPage.GotoAsync("/Collections");
        await Expect(otherPage.GetByText("Finished favourites", new() { Exact = true })).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Remove from collection", Exact = true }).ClickAsync();
        await Expect(page.Locator(".game-grid .game-card")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete collection", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/Collections$"));
        await page.GotoAsync("/Library");
        await Expect(page.Locator(".library-row")).ToContainTextAsync("Browser Quest");
    }

    [Fact]
    public async Task ScreenshotKeyboardNavigationAndOnDemandTrailerWork()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenRemoteGameAsync(page);
        await Expect(page.Locator("iframe")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Link, new() { Name = "Open screenshot 1", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await Expect(page.Locator("[data-screenshot-count]")).ToHaveTextAsync("1 / 2");
        await page.Keyboard.PressAsync("ArrowRight");
        await Expect(page.Locator("[data-screenshot-count]")).ToHaveTextAsync("2 / 2");
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByRole(AriaRole.Dialog)).ToBeHiddenAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Play video", Exact = false }).ClickAsync();
        await Expect(page.Locator("iframe")).ToHaveAttributeAsync("src", "https://www.youtube-nocookie.com/embed/abcDEF12_-3?autoplay=1&rel=0");
        await Expect(page.Locator(".feedback-link")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task StatisticsRenderRealChartsAndStayPrivateToEachAccount()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await RegisterAsync(page);
        await page.GotoAsync("/Library");
        await page.GetByRole(AriaRole.Link, new() { Name = "Statistics", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "More insight into your games", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Account settings", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Upgrade to Premium", Exact = true }).ClickAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "premium-account.png"), FullPage = true });
        await page.GetByRole(AriaRole.Link, new() { Name = "View statistics", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your story starts with one game", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("canvas")).ToHaveCountAsync(0);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("2");
        await page.GetByLabel("Your rating out of 10").FillAsync("8.5");
        await page.GetByLabel("Completed", new() { Exact = true }).FillAsync(DateTime.Today.ToString("yyyy-MM-dd"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Statistics", Exact = true }).ClickAsync();
        await Expect(page.Locator("[data-stat=total]")).ToHaveTextAsync("1");
        await Expect(page.Locator("[data-stat=completed]")).ToHaveTextAsync("1");
        await Expect(page.Locator("[data-stat=average]")).ToHaveTextAsync("8.5/10");
        Assert.Equal(1, await page.EvaluateAsync<int>("Chart.getChart('status-chart').data.datasets[0].data[2]"));
        Assert.Equal(1, await page.EvaluateAsync<int>("Chart.getChart('completion-chart').data.datasets[0].data[11]"));
        Assert.Equal(new[] { "Adventure", "Puzzle" }, await page.EvaluateAsync<string[]>("Chart.getChart('genre-chart').data.labels"));
        Assert.True(await page.EvaluateAsync<bool>("[...document.querySelectorAll('canvas')].every(canvas => canvas.width > 0 && canvas.height > 0 && Chart.getChart(canvas))"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "library-statistics.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForFunctionAsync("document.documentElement.scrollWidth <= window.innerWidth");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "library-statistics-mobile.png"), FullPage = true });
        Assert.Empty(errors);

        await using var visitor = await fixture.NewContextAsync();
        var visitorPage = await visitor.NewPageAsync();
        await RegisterAsync(visitorPage);
        await visitorPage.GotoAsync("/Library/Statistics");
        await Expect(visitorPage.GetByRole(AriaRole.Heading, new() { Name = "More insight into your games", Exact = true })).ToBeVisibleAsync();
        await visitorPage.GotoAsync("/Account/Settings");
        await visitorPage.GetByRole(AriaRole.Button, new() { Name = "Upgrade to Premium", Exact = true }).ClickAsync();
        await visitorPage.GetByRole(AriaRole.Link, new() { Name = "View statistics", Exact = true }).ClickAsync();
        await Expect(visitorPage.GetByRole(AriaRole.Heading, new() { Name = "Your story starts with one game", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CoverPreviewReadsTheLocalFileAndRejectsInvalidImagesBeforeSaving()
    {
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await RegisterAsync(page);
        await page.GotoAsync("/Collections/Create");
        var posts = 0;
        page.Request += (_, request) => { if (request.Method == "POST") posts++; };
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Preview collection");
        await page.GetByLabel("Upload file", new() { Exact = true }).CheckAsync();
        var input = page.GetByLabel("Choose an image", new() { Exact = true });
        await input.SetInputFilesAsync(new FilePayload { Name = "too-large.png", MimeType = "image/png", Buffer = new byte[2 * 1024 * 1024 + 1] });
        await Expect(page.Locator("[data-file-error]")).ToContainTextAsync("up to 2 MB");
        await input.SetInputFilesAsync(new FilePayload { Name = "fake.png", MimeType = "image/png", Buffer = "<svg>Not a PNG</svg>"u8.ToArray() });
        await Expect(page.Locator("[data-file-error]")).ToContainTextAsync("not a PNG or JPEG");
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAIAAAAt/+nTAAAATklEQVR4nO3PUQkAIBTAwJfTOCY2giH8OITBAtxm7fN1wwUNaEEDWtCAFjSgBQ1oQQNa0IAWNKAFDWhBA1rQgBY0oAUNaEEDWtCAFjx2AZFlYNPMtHj6AAAAAElFTkSuQmCC");
        await input.SetInputFilesAsync(new FilePayload { Name = "preview.png", MimeType = "image/png", Buffer = image });
        await Expect(page.GetByAltText("Selected collection cover", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-file-info]")).ToContainTextAsync("64 x 32");
        await Expect(page.Locator("[data-file-error]")).ToBeHiddenAsync();
        await Expect(page.GetByAltText("Selected collection cover", new() { Exact = true })).ToHaveAttributeAsync("src", new Regex("^blob:"));
        Assert.True(await input.EvaluateAsync<bool>("input => input.checkValidity()"));
        Assert.Equal(0, posts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "collection-file-preview.png"), FullPage = true });
        await page.GetByLabel("No cover", new() { Exact = true }).CheckAsync();
        await Expect(page.Locator("[data-file-preview]")).ToBeHiddenAsync();
        await Expect(input).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save collection", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Preview collection", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task JournalsSyncAcrossBrowsersAndPreserveConflictingOfflineDrafts()
    {
        await using var first = await fixture.NewContextAsync();
        var page = await first.NewPageAsync();
        var email = await RegisterAsync(page);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Open play journal", Exact = false }).ClickAsync();
        var url = page.Url;
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        var storageKey = await page.Locator("[data-journal]").GetAttributeAsync("data-storage-key");
        await page.AddInitScriptAsync("""
            const key = STORAGE_KEY;
            sessionStorage.removeItem(key + ':draft');
            localStorage.setItem(key, JSON.stringify({ version: 1, blocks: [{ type: 'paragraph', text: 'Legacy local notes' }] }));
            """.Replace("STORAGE_KEY", System.Text.Json.JsonSerializer.Serialize(storageKey)));
        await page.ReloadAsync();
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");

        await using var second = await fixture.NewContextAsync();
        var remote = await second.NewPageAsync();
        await remote.GotoAsync("/Account/Login");
        await remote.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await remote.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await remote.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await remote.GotoAsync(url);
        await Expect(remote.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("Legacy local notes");
        await page.GetByLabel("Block text", new() { Exact = true }).FillAsync("Live session update");
        await Expect(remote.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("Live session update");

        await second.SetOfflineAsync(true);
        await remote.GetByLabel("Block text", new() { Exact = true }).FillAsync("My offline draft");
        await Expect(remote.Locator("[data-save-status]")).ToContainTextAsync("local draft kept");
        await page.GetByLabel("Block text", new() { Exact = true }).FillAsync("Newer server notes");
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        await second.SetOfflineAsync(false);
        await remote.GetByRole(AriaRole.Button, new() { Name = "Retry sync", Exact = true }).ClickAsync();
        await Expect(remote.Locator("[data-conflict]")).ToBeVisibleAsync();
        await Expect(remote.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("My offline draft");
        await remote.GetByRole(AriaRole.Button, new() { Name = "Save my draft instead", Exact = true }).ClickAsync();
        await Expect(remote.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        await Expect(page.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("My offline draft");
        await second.SetOfflineAsync(true);
        await remote.GetByLabel("Block text", new() { Exact = true }).FillAsync("Draft I will discard");
        await Expect(remote.Locator("[data-save-status]")).ToContainTextAsync("local draft kept");
        await page.GetByLabel("Block text", new() { Exact = true }).FillAsync("Keep the server copy");
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        await second.SetOfflineAsync(false);
        await remote.GetByRole(AriaRole.Button, new() { Name = "Retry sync", Exact = true }).ClickAsync();
        await Expect(remote.Locator("[data-conflict]")).ToBeVisibleAsync();
        await remote.GetByRole(AriaRole.Button, new() { Name = "Load server copy", Exact = true }).ClickAsync();
        await Expect(remote.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("Keep the server copy");
        await remote.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "journal-sync.png"), FullPage = true });
    }

    [Fact]
    public async Task RemoteJournalChangesAnimateAndHighlightWhilePreservingFocus()
    {
        await using var first = await fixture.NewContextAsync();
        var page = await first.NewPageAsync();
        var email = await RegisterAsync(page);
        await OpenRemoteGameAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Open play journal", Exact = false }).ClickAsync();
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        await page.GetByLabel("Block text", new() { Exact = true }).FillAsync("Same session note");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Text", Exact = true }).ClickAsync();
        await page.GetByLabel("Block text", new() { Exact = true }).Last.FillAsync("Same session note");
        await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        var firstId = await page.Locator(".journal-block").First.GetAttributeAsync("data-block-id");

        await using var second = await fixture.NewContextAsync();
        var remote = await second.NewPageAsync();
        await remote.GotoAsync("/Account/Login");
        await remote.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await remote.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await remote.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await remote.GotoAsync(page.Url);
        await Expect(remote.Locator(".journal-block")).ToHaveCountAsync(2);
        await Expect(remote.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
        await remote.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        var normalBorder = await remote.Locator(".journal-block").First.EvaluateAsync<string>("card => getComputedStyle(card).borderColor");
        await remote.GetByLabel("Block text", new() { Exact = true }).Last.FocusAsync();
        await remote.GetByLabel("Block text", new() { Exact = true }).Last.EvaluateAsync("input => input.setSelectionRange(3, 3)");
        var movement = remote.WaitForFunctionAsync("id => [...document.querySelectorAll('.journal-block')].find(card => card.dataset.blockId === id)?.getAnimations().some(animation => animation.playState === 'running')", firstId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Move block down", Exact = true }).First.ClickAsync();
        await movement;
        await Expect(remote.Locator(".journal-block").Last).ToHaveAttributeAsync("data-block-id", firstId!);
        await Expect(remote.Locator(".journal-block").Last).ToHaveAttributeAsync("data-remote-change", new Regex("moved"));
        await Expect(remote.GetByLabel("Block text", new() { Exact = true }).First).ToBeFocusedAsync();
        Assert.Equal(3, await remote.GetByLabel("Block text", new() { Exact = true }).First.EvaluateAsync<int>("input => input.selectionStart"));
        Assert.NotEqual(normalBorder, await remote.Locator(".journal-block").Last.EvaluateAsync<string>("card => getComputedStyle(card).borderColor"));

        await page.GetByLabel("Block text", new() { Exact = true }).Last.FillAsync("Updated from the other browser");
        await Expect(remote.GetByLabel("Block text", new() { Exact = true }).Last).ToHaveValueAsync("Updated from the other browser");
        await Expect(remote.Locator(".journal-block").Last).ToHaveAttributeAsync("data-remote-change", new Regex("edited"));
        await remote.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "journal-remote-actions.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove block", Exact = true }).Last.ClickAsync();
        await Expect(remote.Locator(".journal-block")).ToHaveCountAsync(1);
        await Expect(remote.Locator("[data-remote-notice]")).ToContainTextAsync("removed");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Heading", Exact = true }).ClickAsync();
        await Expect(remote.Locator(".journal-block")).ToHaveCountAsync(2);
        await Expect(remote.Locator(".journal-block").Last).ToHaveAttributeAsync("data-remote-change", new Regex("added"));
        var headingId = await remote.Locator(".journal-block").Last.GetAttributeAsync("data-block-id");
        await remote.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.GetByRole(AriaRole.Button, new() { Name = "Move block up", Exact = true }).Last.ClickAsync();
        await Expect(remote.Locator(".journal-block").First).ToHaveAttributeAsync("data-block-id", headingId!);
        await Expect(remote.Locator(".journal-block").First).ToHaveAttributeAsync("data-remote-change", new Regex("moved"));
        Assert.Equal(0, await remote.Locator(".journal-block").First.EvaluateAsync<int>("card => card.getAnimations().length"));
    }

    [Fact]
    public async Task JournalBlocksCanBeDraggedExportedAndRestoredAfterBrowserRestart()
    {
        var root = Path.GetFullPath(fixture.ResultsPath) + Path.DirectorySeparatorChar;
        var profile = Path.GetFullPath(Path.Combine(root, "journal-profile-" + Guid.NewGuid().ToString("N")));
        IBrowserContext? context = null;
        try
        {
            context = await fixture.OpenProfileAsync(profile);
            var page = await context.NewPageAsync();
            var email = await RegisterAsync(page);
            await OpenRemoteGameAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
            await page.GetByRole(AriaRole.Link, new() { Name = "Open play journal", Exact = false }).ClickAsync();
            var journalUrl = page.Url;
            await page.GetByLabel("Block text", new() { Exact = true }).FillAsync("Session one <img src=x onerror=alert(1)>");
            await page.GetByRole(AriaRole.Button, new() { Name = "+ Heading", Exact = true }).ClickAsync();
            await page.GetByLabel("Block text", new() { Exact = true }).Nth(1).FillAsync("Next session");
            await page.GetByRole(AriaRole.Button, new() { Name = "+ List", Exact = true }).ClickAsync();
            await page.GetByLabel("Block text", new() { Exact = true }).Nth(2).FillAsync("Finish main story\nFind both endings");
            await page.GetByRole(AriaRole.Button, new() { Name = "Drag block", Exact = true }).Nth(2)
                .DragToAsync(page.Locator(".journal-block").First, new() { TargetPosition = new() { X = 10, Y = 10 } });
            await Expect(page.GetByLabel("Block text", new() { Exact = true }).First).ToHaveValueAsync("Finish main story\nFind both endings");
            await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
            await context.CloseAsync();
            context = null;

            context = await fixture.OpenProfileAsync(profile);
            page = await context.NewPageAsync();
            await page.GotoAsync("/Account/Login");
            await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(journalUrl);
            await Expect(page.Locator(".journal-block")).ToHaveCountAsync(3);
            await Expect(page.GetByLabel("Block text", new() { Exact = true }).First).ToHaveValueAsync("Finish main story\nFind both endings");
            await Expect(page.GetByLabel("Block text", new() { Exact = true }).Nth(2)).ToHaveValueAsync("Next session");
            await page.GetByLabel("Show preview", new() { Exact = true }).CheckAsync();
            await Expect(page.Locator("[data-rendered] li")).ToHaveCountAsync(2);
            await Expect(page.Locator("[data-rendered] img")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-rendered]")).ToContainTextAsync("<img src=x onerror=alert(1)>");
            await page.GetByRole(AriaRole.Button, new() { Name = "Move block up", Exact = true }).Nth(2).ClickAsync();
            await Expect(page.GetByLabel("Block text", new() { Exact = true }).Nth(1)).ToHaveValueAsync("Next session");
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Export Markdown", Exact = true }).ClickAsync());
            var exported = Path.Combine(fixture.ResultsPath, "play-journal.md");
            await download.SaveAsAsync(exported);
            var markdown = await File.ReadAllTextAsync(exported);
            Assert.StartsWith("- Finish main story\n- Find both endings", markdown);
            Assert.Contains("# Next session", markdown);
            await page.GetByLabel("Block text", new() { Exact = true }).Nth(2).FillAsync("Finished the first act. Next time I want to explore the optional areas.");
            await Expect(page.Locator("[data-save-status]")).ToHaveTextAsync("Synced to your account");
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "play-journal.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("document.documentElement.scrollWidth <= window.innerWidth");
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ResultsPath, "play-journal-mobile.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
            await RegisterAsync(page);
            Assert.Equal(404, (await context.APIRequest.GetAsync(journalUrl)).Status);
            await OpenRemoteGameAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add to my library +", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Open play journal", Exact = false }).ClickAsync();
            await Expect(page.Locator(".journal-block")).ToHaveCountAsync(1);
            await Expect(page.GetByLabel("Block text", new() { Exact = true })).ToHaveValueAsync("");
        }
        finally
        {
            if (context is not null) await context.CloseAsync();
            if (profile.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(profile))
                Directory.Delete(profile, true);
        }
    }
}
