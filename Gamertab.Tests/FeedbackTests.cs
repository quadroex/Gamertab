using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class FeedbackFactory : AppFactory
{
    public FeedbackHandler Handler { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Crm:AccessToken"] = "test-crm-token" }));
        builder.ConfigureServices(services => services.AddHttpClient<HubSpotClient>().ConfigurePrimaryHttpMessageHandler(() => Handler));
    }
}

public class FeedbackHandler : HttpMessageHandler
{
    public bool Fail { get; set; }
    public string Submitted { get; private set; } = "";
    public int TicketReads { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-crm-token", request.Headers.Authorization?.Parameter);
        if (Fail) return new(HttpStatusCode.ServiceUnavailable);
        object result;
        if (request.Method == HttpMethod.Post)
        {
            Submitted = await request.Content!.ReadAsStringAsync(cancellationToken);
            result = new { id = "987" };
        }
        else if (request.RequestUri!.AbsolutePath.Contains("pipelines"))
            result = new { results = new[] { new { id = "0", stages = new[] { new { id = "4", label = "Closed" } } } } };
        else
        {
            TicketReads++;
            result = new { properties = new { hs_pipeline = "0", hs_pipeline_stage = "4", content = "Support request body" } };
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
    }
}

public class FeedbackTests
{
    private static async Task<HttpClient> RegisterAsync(FeedbackFactory factory, string email)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        async Task Token()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        }
        await Token();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/account/register", new { email, password = "GamertabTest42", confirmPassword = "GamertabTest42" })).StatusCode);
        await Token();
        return client;
    }

    private static FormUrlEncodedContent Input() => new(new Dictionary<string, string>
    {
        ["Category"] = "Bug", ["Subject"] = "A helpful report", ["Message"] = "A problem that needs investigation", ["GameId"] = "1"
    });

    [Fact]
    public async Task FeedbackCreatesRealCrmRequestAndReadsStatusOnlyForItsOwner()
    {
        using var factory = new FeedbackFactory();
        using var owner = await RegisterAsync(factory, "feedback-owner@example.test");
        var sent = await owner.PostAsync("/Feedback/Send", Input());
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.StartsWith("/Feedback/Details/", sent.Headers.Location!.ToString());
        Assert.Contains("A problem that needs investigation", factory.Handler.Submitted);
        Assert.Contains("/Games/Details/1", factory.Handler.Submitted);
        Assert.DoesNotContain("feedback-owner@example.test", factory.Handler.Submitted);
        var details = await owner.GetStringAsync(sent.Headers.Location);
        Assert.Contains("Closed", details);
        Assert.Contains("Support request body", details);
        Assert.Contains("A helpful report", await owner.GetStringAsync("/Feedback"));
        var reads = factory.Handler.TicketReads;
        using var other = await RegisterAsync(factory, "feedback-other@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(sent.Headers.Location)).StatusCode);
        Assert.Equal(reads, factory.Handler.TicketReads);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/Feedback/Send", Input())).StatusCode);
    }

    [Fact]
    public async Task FailedDeliveryPreservesFormWithoutClaimingItWasSent()
    {
        using var factory = new FeedbackFactory();
        factory.Handler.Fail = true;
        using var owner = await RegisterAsync(factory, "feedback-failure@example.test");
        var result = await owner.PostAsync("/Feedback/Send", Input());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains("did not confirm delivery", html);
        Assert.Contains("A problem that needs investigation", html);
        Assert.DoesNotContain("Your recent requests", html);
    }
}
