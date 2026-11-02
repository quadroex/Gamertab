using System.Net;
using System.Net.Http.Json;
using Azure.Storage.Queues;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Tests;

public class QueueFactAttribute : FactAttribute
{
    public QueueFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMERTAB_QUEUE_TEST_CONNECTION")))
            Skip = "Run dev.ps1 queue-test to check the real local queue.";
    }
}

public class NewsQueueFactory : AppFactory
{
    public NewsFeedHandler Feed { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewsUpdates:RetryDelaySeconds"] = "0"
        }));
        builder.ConfigureServices(services => services.AddHttpClient<SteamNewsClient>().ConfigurePrimaryHttpMessageHandler(() => Feed));
    }

    public NewsUpdateWorker Worker(IConfiguration? configuration = null) => new(
        Services.GetRequiredService<IServiceScopeFactory>(), configuration ?? Services.GetRequiredService<IConfiguration>(),
        Services.GetRequiredService<ILogger<NewsUpdateWorker>>());

    public async Task FollowAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Users.AddRange(new IdentityUser { Id = "news-owner", UserName = "news-owner" }, new IdentityUser { Id = "news-other", UserName = "news-other" });
        database.LibraryEntries.AddRange(
            new LibraryEntry { UserId = "news-owner", GameId = 1, FollowNews = true },
            new LibraryEntry { UserId = "news-other", GameId = 1, FollowNews = true },
            new LibraryEntry { UserId = "news-owner", GameId = 2, FollowNews = false });
        await database.SaveChangesAsync();
    }

    public async Task<int> NewsCountAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsItems.CountAsync();
    }
}

public class NewsFeedHandler : HttpMessageHandler
{
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Requested.TrySetResult();
        if (Fail)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { appnews = new { newsitems = new[] {
                new { gid = "queue-test-news", title = "A new game update", url = "https://store.steampowered.com/news/app/620/view/1", feedlabel = "Steam", date = 1793530800 }
            } } })
        });
    }
}

public sealed class TestQueues : IAsyncDisposable
{
    public string Name { get; } = "gamertab-test-" + Guid.NewGuid().ToString("N");
    public QueueClient Pending { get; }
    public QueueClient Failed { get; }

    public TestQueues()
    {
        var connection = Environment.GetEnvironmentVariable("GAMERTAB_QUEUE_TEST_CONNECTION")!;
        var options = new QueueClientOptions(QueueClientOptions.ServiceVersion.V2023_11_03);
        Pending = new QueueClient(connection, Name, options);
        Failed = new QueueClient(connection, Name + "-failed", options);
    }

    public async Task CreateAsync()
    {
        await Pending.CreateIfNotExistsAsync();
        await Failed.CreateIfNotExistsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Pending.DeleteIfExistsAsync();
        await Failed.DeleteIfExistsAsync();
    }
}

public class NewsQueueTests
{
    [QueueFact]
    public async Task FollowedGamesAreQueuedOnceAndRepeatedDeliveryDoesNotDuplicateNews()
    {
        using var factory = new NewsQueueFactory();
        await factory.FollowAsync();
        await using var queues = new TestQueues();
        await queues.CreateAsync();
        using var worker = factory.Worker();

        Assert.Equal(1, await worker.EnqueueFollowedAsync(queues.Pending, CancellationToken.None));
        Assert.Equal(0, await worker.EnqueueFollowedAsync(queues.Pending, CancellationToken.None));
        Assert.True(await worker.ProcessNextAsync(queues.Pending, queues.Failed, CancellationToken.None));
        Assert.Equal(1, await factory.NewsCountAsync());
        Assert.Equal(0, (await queues.Pending.GetPropertiesAsync()).Value.ApproximateMessagesCount);

        factory.Services.GetRequiredService<IMemoryCache>().Remove("steam-news:1");
        await queues.Pending.SendMessageAsync("1");
        Assert.True(await worker.ProcessNextAsync(queues.Pending, queues.Failed, CancellationToken.None));
        Assert.Equal(2, factory.Feed.Calls);
        Assert.Equal(1, await factory.NewsCountAsync());
    }

    [QueueFact]
    public async Task UnfollowedGamesAreSkippedAndInvalidJobsArePreservedForReview()
    {
        using var factory = new NewsQueueFactory();
        await factory.FollowAsync();
        await using var queues = new TestQueues();
        await queues.CreateAsync();
        using var worker = factory.Worker();
        await worker.EnqueueFollowedAsync(queues.Pending, CancellationToken.None);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().LibraryEntries.ExecuteUpdateAsync(update => update.SetProperty(entry => entry.FollowNews, false));
        Assert.True(await worker.ProcessNextAsync(queues.Pending, queues.Failed, CancellationToken.None));
        Assert.Equal(0, factory.Feed.Calls);
        Assert.Equal(0, await factory.NewsCountAsync());

        await queues.Pending.SendMessageAsync("invalid-job");
        Assert.True(await worker.ProcessNextAsync(queues.Pending, queues.Failed, CancellationToken.None));
        Assert.Equal("invalid-job", (await queues.Failed.PeekMessagesAsync()).Value.Single().Body.ToString());
        Assert.Equal(0, (await queues.Pending.GetPropertiesAsync()).Value.ApproximateMessagesCount);
    }

    [QueueFact]
    public async Task FailedRequestsAreRetriedThreeTimesBeforeMovingToTheFailedQueue()
    {
        using var factory = new NewsQueueFactory();
        factory.Feed.Fail = true;
        await factory.FollowAsync();
        await using var queues = new TestQueues();
        await queues.CreateAsync();
        using var worker = factory.Worker();
        await worker.EnqueueFollowedAsync(queues.Pending, CancellationToken.None);
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.True(await worker.ProcessNextAsync(queues.Pending, queues.Failed, CancellationToken.None));
        Assert.Equal(3, factory.Feed.Calls);
        Assert.Equal(0, await factory.NewsCountAsync());
        Assert.Equal(0, (await queues.Pending.GetPropertiesAsync()).Value.ApproximateMessagesCount);
        Assert.Equal("1", (await queues.Failed.PeekMessagesAsync()).Value.Single().Body.ToString());
    }

    [QueueFact]
    public async Task BackgroundLoopUpdatesNewsWithoutOpeningAGamePage()
    {
        using var factory = new NewsQueueFactory();
        await factory.FollowAsync();
        await using var queues = new TestQueues();
        await queues.CreateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewsUpdates:Enabled"] = "true",
            ["NewsUpdates:QueueName"] = queues.Name,
            ["ConnectionStrings:QueueStorage"] = Environment.GetEnvironmentVariable("GAMERTAB_QUEUE_TEST_CONNECTION")
        }).Build();
        using var worker = factory.Worker(configuration);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await factory.Feed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while ((await queues.Pending.GetPropertiesAsync()).Value.ApproximateMessagesCount != 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, await factory.NewsCountAsync());
        Assert.Equal(0, (await queues.Pending.GetPropertiesAsync()).Value.ApproximateMessagesCount);
    }
}
