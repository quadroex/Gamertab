using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Data;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class GoogleFactory : AppFactory
{
    protected override bool GoogleEnabled => true;
    public GoogleBackchannel Backchannel { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.PostConfigure<GoogleOptions>("Google", options =>
            options.Backchannel = new HttpClient(Backchannel)));
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
    });
}

public class GoogleBackchannel : HttpMessageHandler
{
    public const string Email = "google-player@example.test";
    public bool EmailVerified { get; set; } = true;
    public int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.RequestUri!.AbsolutePath == "/token")
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("gamertab-test-client", form["client_id"]);
            Assert.False(string.IsNullOrEmpty(form["code_verifier"]));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = "test-access-token", token_type = "Bearer", expires_in = 3600 })
            };
        }
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { sub = "google-player-123", email = Email, email_verified = EmailVerified, name = "Test Player" })
        };
    }
}

public class GoogleLoginTests
{
    private static async Task SetCsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    private static async Task<string> StartAsync(HttpClient client, string path = "/Account/GoogleLogin", string? returnUrl = null)
    {
        await SetCsrfAsync(client);
        var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["returnUrl"] = returnUrl ?? "/Library"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("email", query["scope"].ToString());
        return query["state"].ToString();
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, string state)
    {
        var response = await client.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string, string?>
        {
            ["code"] = "test-code", ["state"] = state
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await client.GetAsync(response.Headers.Location);
    }

    private static async Task RegisterLocalAsync(HttpClient client, string email = GoogleBackchannel.Email)
    {
        await SetCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/account/register", new
        {
            email, password = "GamertabTest2026", confirmPassword = "GamertabTest2026"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task LogoutAsync(HttpClient client)
    {
        await SetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/account/logout", null)).StatusCode);
    }

    [Fact]
    public async Task GoogleCreatesAnAccountAndReusesItOnTheNextLogin()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        var first = await CompleteAsync(client, await StartAsync(client));
        Assert.Equal("/Library", first.Headers.Location?.OriginalString);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/account/session")).GetProperty("authenticated").GetBoolean());
        await LogoutAsync(client);
        await CompleteAsync(client, await StartAsync(client));
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.Equal(1, await database.UserLogins.CountAsync());
    }

    [Fact]
    public async Task MatchingEmailDoesNotAutomaticallyLinkAnExistingAccount()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        await RegisterLocalAsync(client);
        await LogoutAsync(client);
        var response = await CompleteAsync(client, await StartAsync(client));
        Assert.Equal("/Account/Login", response.Headers.Location?.OriginalString);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/session")).GetProperty("authenticated").GetBoolean());
        Assert.Contains("already exists", await client.GetStringAsync("/Account/Login"));
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await database.Users.CountAsync());
        Assert.Equal(0, await database.UserLogins.CountAsync());
    }

    [Fact]
    public async Task SignedInUserCanConnectGoogleAndKeepTheSameLibrary()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        await RegisterLocalAsync(client);
        await SetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/library", new { gameId = 1, status = "Playing" })).StatusCode);
        var linked = await CompleteAsync(client, await StartAsync(client, "/Account/LinkGoogle"));
        Assert.Equal("/Account/Settings", linked.Headers.Location?.OriginalString);
        Assert.Contains("Google is connected", await client.GetStringAsync("/Account/Settings"));
        await LogoutAsync(client);
        await CompleteAsync(client, await StartAsync(client));
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/library")).GetArrayLength());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task GoogleConnectionCannotBeTransferredToAnotherSignedInUser()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        await RegisterLocalAsync(client);
        var state = await StartAsync(client, "/Account/LinkGoogle");
        await LogoutAsync(client);
        await RegisterLocalAsync(client, "another-player@example.test");
        await CompleteAsync(client, state);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await database.Users.CountAsync());
        Assert.Equal(0, await database.UserLogins.CountAsync());
        Assert.Equal("another-player@example.test", (await client.GetFromJsonAsync<JsonElement>("/api/account/session")).GetProperty("email").GetString());
    }

    [Fact]
    public async Task UnverifiedGoogleEmailCannotCreateAnAccount()
    {
        using var factory = new GoogleFactory();
        factory.Backchannel.EmailVerified = false;
        using var client = factory.Browser();
        var response = await CompleteAsync(client, await StartAsync(client));
        Assert.Equal("/Account/Login", response.Headers.Location?.OriginalString);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task InvalidStateIsRejectedBeforeContactingGoogle()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        var response = await client.GetAsync("/signin-google?code=test-code&state=invalid");
        Assert.Equal("/Account/Login?externalError=true", response.Headers.Location?.OriginalString);
        Assert.Equal(0, factory.Backchannel.Requests);
    }

    [Fact]
    public async Task CallbackFromAnotherBrowserCannotReuseTheState()
    {
        using var factory = new GoogleFactory();
        using var original = factory.Browser();
        using var other = factory.Browser();
        var state = await StartAsync(original);
        var response = await other.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string, string?>
        {
            ["code"] = "test-code", ["state"] = state
        }));
        Assert.Equal("/Account/Login?externalError=true", response.Headers.Location?.OriginalString);
        Assert.Equal(0, factory.Backchannel.Requests);
    }

    [Fact]
    public async Task CancelledGoogleLoginDoesNotSignIn()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        var state = await StartAsync(client);
        var response = await client.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string, string?>
        {
            ["error"] = "access_denied", ["state"] = state
        }));
        Assert.Equal("/Account/Login?externalError=true", response.Headers.Location?.OriginalString);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/session")).GetProperty("authenticated").GetBoolean());
    }

    [Theory]
    [InlineData("https://example.test/steal")]
    [InlineData("//example.test/steal")]
    public async Task GoogleLoginDoesNotRedirectToAnExternalSite(string returnUrl)
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        var response = await CompleteAsync(client, await StartAsync(client, returnUrl: returnUrl));
        Assert.Equal("/Library", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GoogleLoginRequiresCsrfAndCallbacksRequireAnExternalSession()
    {
        using var factory = new GoogleFactory();
        using var client = factory.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Account/GoogleLogin", null)).StatusCode);
        Assert.Equal("/Account/Login", (await client.GetAsync("/Account/GoogleCallback")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PasswordLoginRejectsWrongPasswordAndLogoutEndsTheSession()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RegisterLocalAsync(client);
        await LogoutAsync(client);
        await SetCsrfAsync(client);
        var wrong = await client.PostAsJsonAsync("/api/account/login", new { email = GoogleBackchannel.Email, password = "WrongPassword123" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var correct = await client.PostAsJsonAsync("/api/account/login", new { email = GoogleBackchannel.Email, password = "GamertabTest2026" });
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        await SetCsrfAsync(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/account/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/library")).StatusCode);
        await LogoutAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/library")).StatusCode);
    }
}
