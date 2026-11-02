using System.Net;
using System.Net.Http.Json;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class MediaFactory : AppFactory
{
    protected override bool IgdbEnabled => true;
    public MediaHandler Handler { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => Handler));
    }
}

public class MediaHandler : HttpMessageHandler
{
    public bool Fail { get; set; }
    public bool Empty { get; set; }
    public int MediaRequests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "id.twitch.tv")
            return Json(new { access_token = "media-test-token", expires_in = 3600 });
        var query = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (query.Contains("screenshots.image_id"))
        {
            MediaRequests++;
            if (Fail)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Empty)
                return Json(new[] { new { id = 1001 } });
            var screenshots = Enumerable.Range(0, 9).Select(index => new { image_id = $"sc{index}" }).Cast<object>()
                .Concat([new { image_id = "sc0" }, new { image_id = "../untrusted" }]).ToArray();
            return Json(new[] { new
            {
                id = 1001, screenshots,
                videos = new[]
                {
                    new { name = "Gameplay", video_id = "ZYXwvut9876" },
                    new { name = "Launch trailer", video_id = "abcDEF12_-3" },
                    new { name = "Untrusted trailer", video_id = "javascript:bad" }
                }
            }});
        }
        if (request.RequestUri.AbsolutePath.EndsWith("game_time_to_beats"))
            return Json(Array.Empty<object>());
        return Json(new[] { new { id = 1001, name = "Media Test Game", summary = "Original summary stays visible." } });
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

public class GameMediaTests
{
    [Fact]
    public async Task MediaUsesTrustedCdnLinksAndSelectsATrailerWithoutDuplicateRequests()
    {
        using var factory = new MediaFactory();
        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IgdbClient>();
        var media = await client.GetMediaAsync(1001);
        Assert.Equal(6, media.Screenshots.Count);
        Assert.All(media.Screenshots, image => Assert.StartsWith("https://images.igdb.com/igdb/image/upload/t_1080p/", image.FullUrl));
        Assert.Equal(6, media.Screenshots.Select(image => image.FullUrl).Distinct().Count());
        Assert.Equal("abcDEF12_-3", media.Video!.YouTubeId);
        Assert.Equal("Launch trailer", media.Video.Title);
        await client.GetMediaAsync(1001);
        Assert.Equal(1, factory.Handler.MediaRequests);
    }

    [Fact]
    public async Task DetailsIncludeMediaAndFeedbackWithoutLoadingTheYouTubePlayer()
    {
        using var factory = new MediaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var imported = await client.GetAsync("/Games/Open/1001");
        var html = await client.GetStringAsync(imported.Headers.Location);
        Assert.Contains("Original summary stays visible.", html);
        Assert.Contains("Screenshots", html);
        Assert.Contains("Play video", html);
        Assert.Contains("data-video-id=\"abcDEF12_-3\"", html);
        Assert.Contains("https://github.com/quadroex/Gamertab/issues", html);
        Assert.Contains("href=\"#media\"", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("javascript:bad", html);
        Assert.DoesNotContain("../untrusted", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrMissingMediaDoesNotBreakTheGamePage(bool failed)
    {
        using var factory = new MediaFactory();
        factory.Handler.Fail = failed;
        factory.Handler.Empty = !failed;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var imported = await client.GetAsync("/Games/Open/1001");
        var html = await client.GetStringAsync(imported.Headers.Location);
        Assert.Contains("Media Test Game", html);
        Assert.Contains("Original summary stays visible.", html);
        Assert.Contains("How long to play?", html);
        Assert.DoesNotContain("Play video", html);
        Assert.DoesNotContain("href=\"#media\"", html);
        if (failed)
            Assert.Contains("Game media is temporarily unavailable", html);
    }
}
