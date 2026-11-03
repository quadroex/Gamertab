using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Gamertab.Tests;

public class PlanFactory : AppFactory
{
    public bool StatisticsEnabled { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Premium:LibraryStatistics"] = StatisticsEnabled.ToString()
        }));
        builder.ConfigureServices(services => services.AddScoped(provider => new PlanAccess(
            provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<IConfiguration>(),
            new HostingEnvironment { EnvironmentName = Environments.Development })));
    }
}

public class PlanAccessTests
{
    private class ConfiguredDemoFactory : AppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:AllowDemoPlanChanges"] = "true" }));
        }
    }

    [Fact]
    public async Task ExplicitDemoFlagEnablesUpgradeOutsideDevelopmentWithoutChangingOtherAccounts()
    {
        using var factory = new ConfiguredDemoFactory();
        var (owner, _) = await RegisterAsync(factory);
        var (other, _) = await RegisterAsync(factory);
        using (owner)
        using (other)
        {
            Assert.Contains("Upgrade to Premium", await owner.GetStringAsync("/Account/Settings"));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(owner, "Premium")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(owner, "Standard")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Library/Statistics")).StatusCode);
        }
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    private static async Task<(HttpClient Client, string Email)> RegisterAsync(AppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RefreshCsrfAsync(client);
        var email = $"plan-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/account/register", new { email, password = "GamertabTest42", confirmPassword = "GamertabTest42" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await RefreshCsrfAsync(client);
        return (client, email);
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string plan) =>
        client.PostAsync("/Account/ChangePlan", new FormUrlEncodedContent(new Dictionary<string, string> { ["plan"] = plan }));

    [Fact]
    public async Task StandardCannotReadStatisticsAndUpgradePersistsAfterLogin()
    {
        using var factory = new PlanFactory();
        var (client, email) = await RegisterAsync(factory);
        using (client)
        {
            var denied = await client.GetAsync("/Library/Statistics");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            var html = await denied.Content.ReadAsStringAsync();
            Assert.Contains("Premium feature", html);
            Assert.DoesNotContain("library-statistics\"", html);
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(client, "Premium")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/account/logout", null)).StatusCode);
            await RefreshCsrfAsync(client);
            var login = await client.PostAsJsonAsync("/api/account/login", new { email, password = "GamertabTest42" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Library/Statistics")).StatusCode);
        }
    }

    [Fact]
    public async Task DemoPlanChangesRequireCsrfAndOnlyAffectTheCurrentAccount()
    {
        using var factory = new PlanFactory();
        var (owner, _) = await RegisterAsync(factory);
        var (other, _) = await RegisterAsync(factory);
        using (owner)
        using (other)
        {
            owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(owner, "Premium")).StatusCode);
            await RefreshCsrfAsync(owner);
            Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(owner, "999")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(owner, "Premium")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/Library/Statistics")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(owner, "Standard")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Library/Statistics")).StatusCode);
        }
    }

    [Fact]
    public async Task NonDevelopmentDoesNotExposeSelfServicePlanChanges()
    {
        using var factory = new AppFactory();
        var (client, _) = await RegisterAsync(factory);
        using (client)
        {
            var html = await client.GetStringAsync("/Account/Settings");
            Assert.DoesNotContain("Upgrade to Premium", html);
            Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(client, "Premium")).StatusCode);
        }
    }

    [Fact]
    public async Task DisablingTheFeatureFlagAlsoBlocksPremiumAccounts()
    {
        using var factory = new PlanFactory { StatisticsEnabled = false };
        var (client, _) = await RegisterAsync(factory);
        using (client)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await ChangeAsync(client, "Premium")).StatusCode);
            var response = await client.GetAsync("/Library/Statistics");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("not enabled right now", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task JournalIsAvailableToStandardButOnlyForOwnedLibraryEntries()
    {
        using var factory = new PlanFactory();
        var (owner, _) = await RegisterAsync(factory);
        var (other, _) = await RegisterAsync(factory);
        using (owner)
        using (other)
        {
            var created = await owner.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Planned" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            var response = await owner.GetAsync($"/Library/Journal/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Library/Journal/{id}")).StatusCode);
        }
    }
}
