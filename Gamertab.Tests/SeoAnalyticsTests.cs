using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Gamertab.Tests;

public class SeoTests
{
    [Fact]
    public async Task PublicPagesHaveSpecificMetadataAndSitemapExcludesPrivatePages()
    {
        using var factory = new MediaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var imported = await client.GetAsync("/Games/Open/1001");
        var details = await client.GetStringAsync(imported.Headers.Location);
        Assert.Contains("Explore Media Test Game: platforms, ratings, playtime and news.", details);
        Assert.Contains("rel=\"canonical\"", details);
        Assert.Contains("property=\"og:description\"", details);
        Assert.DoesNotContain("noindex", details);
        Assert.DoesNotContain("analytics.js", details);
        var home = await client.GetStringAsync("/");
        Assert.Contains("Discover popular games across PC and consoles.", home);
        Assert.DoesNotContain("noindex", home);
        Assert.Contains("noindex, nofollow", await client.GetStringAsync("/?q=Portal"));
        Assert.Contains("noindex, nofollow", await client.GetStringAsync("/Account/Login"));
        var sitemap = XDocument.Parse(await client.GetStringAsync("/sitemap.xml"));
        var urls = sitemap.Descendants(XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9")).Select(element => element.Value).ToList();
        Assert.Contains("https://gamertab-917646089327.us-east1.run.app/", urls);
        Assert.Contains("https://gamertab-917646089327.us-east1.run.app" + imported.Headers.Location, urls);
        Assert.All(urls, url => Assert.DoesNotContain("Library", url));
        var robots = await client.GetStringAsync("/robots.txt");
        Assert.Contains("Disallow: /Account/", robots);
        Assert.Contains("Sitemap: https://gamertab-917646089327.us-east1.run.app/sitemap.xml", robots);
    }
}

public class AnalyticsFactory : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analytics:MeasurementId"] = "G-GAMERTABTEST", ["Analytics:EnableInDevelopment"] = "true"
        }));
    }
}

[Trait("Category", "Browser")]
public class AnalyticsBrowserTests
{
    [Fact]
    public async Task AnalyticsRequiresConsentUsesSanitizedUrlsAndCanBeDisabled()
    {
        await using var factory = new AnalyticsFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        await using var context = await browser.NewContextAsync(new() { BaseURL = client.BaseAddress!.ToString() });
        var loads = 0;
        await context.RouteAsync("https://www.googletagmanager.com/**", async route =>
        {
            loads++;
            await route.FulfillAsync(new() { ContentType = "application/javascript", Body = "window.googleTagLoaded = true;" });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/?q=private-search-text");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Allow analytics", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, loads);
        await page.GetByRole(AriaRole.Button, new() { Name = "Decline", Exact = true }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator("[data-analytics]")).ToBeHiddenAsync();
        Assert.Equal(0, loads);
        await page.GetByRole(AriaRole.Button, new() { Name = "Analytics preferences" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Allow analytics", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("window.googleTagLoaded === true");
        Assert.Equal(1, loads);
        var data = await page.EvaluateAsync<string>("JSON.stringify(window.dataLayer)");
        Assert.DoesNotContain("private-search-text", data);
        Assert.Contains("send_page_view", data);
        await page.GotoAsync("/");
        await page.WaitForFunctionAsync("window.googleTagLoaded === true");
        Assert.Contains("page_view", await page.EvaluateAsync<string>("JSON.stringify(window.dataLayer)"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Analytics preferences" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Decline", Exact = true }).ClickAsync();
        Assert.True(await page.EvaluateAsync<bool>("window['ga-disable-G-GAMERTABTEST'] === true"));
        var previousLoads = loads;
        await page.ReloadAsync();
        Assert.Equal(previousLoads, loads);
    }
}
