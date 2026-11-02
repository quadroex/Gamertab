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

public class BrowserFactory : AppFactory
{
    protected override bool IgdbEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:BlobStorage"] = "",
            ["ConnectionStrings:QueueStorage"] = ""
        }));
        builder.ConfigureServices(services =>
        {
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
        await context.RouteAsync("**/*", async route =>
        {
            if (route.Request.Url.StartsWith(BaseUrl, StringComparison.Ordinal))
                await route.ContinueAsync();
            else if (route.Request.ResourceType == "image")
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4XcAAAAASUVORK5CYII=") });
            else
                await route.FulfillAsync(new() { ContentType = "text/html", Body = "<html><title>Test video</title></html>" });
        });
        return context;
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
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Feedback", Exact = false })).ToBeVisibleAsync();
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
        await Expect(visitorPage.GetByRole(AriaRole.Heading, new() { Name = "Your story starts with one game", Exact = true })).ToBeVisibleAsync();
    }
}
