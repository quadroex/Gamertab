using System.Globalization;
using Azure.Storage.Queues;
using Gamertab.Data;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class NewsUpdateWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<NewsUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("NewsUpdates:Enabled"))
            return;

        var connection = configuration.GetConnectionString("QueueStorage");
        if (string.IsNullOrWhiteSpace(connection))
        {
            logger.LogWarning("Background news updates require a QueueStorage connection string.");
            return;
        }
        var options = new QueueClientOptions(QueueClientOptions.ServiceVersion.V2023_11_03);
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        var name = configuration["NewsUpdates:QueueName"] ?? "gamertab-news";
        var queue = new QueueClient(connection, name, options);
        var failed = new QueueClient(connection, name + "-failed", options);
        var scanInterval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("NewsUpdates:ScanIntervalMinutes", 15.0), 0.01, 1440));
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("NewsUpdates:PollIntervalSeconds", 5), 1, 60));
        var nextScan = DateTimeOffset.MinValue;
        var initialized = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    await failed.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    initialized = true;
                    logger.LogInformation("Background news worker connected to queue {QueueName}.", name);
                }
                if (DateTimeOffset.UtcNow >= nextScan)
                {
                    await EnqueueFollowedAsync(queue, stoppingToken);
                    nextScan = DateTimeOffset.UtcNow + scanInterval;
                }
                if (!await ProcessNextAsync(queue, failed, stoppingToken))
                    await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                initialized = false;
                logger.LogWarning(exception, "Background news updates are temporarily unavailable. Retrying later.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task<int> EnqueueFollowedAsync(QueueClient queue, CancellationToken cancellationToken)
    {
        if ((await queue.GetPropertiesAsync(cancellationToken)).Value.ApproximateMessagesCount > 0)
            return 0;
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gameIds = await database.LibraryEntries.Where(entry => entry.FollowNews && entry.Game.SteamAppId != null)
            .Select(entry => entry.GameId).Distinct().ToListAsync(cancellationToken);
        foreach (var gameId in gameIds)
            await queue.SendMessageAsync(gameId.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
        if (gameIds.Count > 0)
            logger.LogInformation("Queued news updates for {GameCount} followed games.", gameIds.Count);
        return gameIds.Count;
    }

    public async Task<bool> ProcessNextAsync(QueueClient queue, QueueClient failed, CancellationToken cancellationToken)
    {
        var message = (await queue.ReceiveMessagesAsync(1, TimeSpan.FromMinutes(2), cancellationToken)).Value.FirstOrDefault();
        if (message is null)
            return false;
        if (!int.TryParse(message.Body.ToString(), out var gameId) || gameId <= 0)
        {
            await failed.SendMessageAsync(message.Body, cancellationToken: cancellationToken);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
            logger.LogWarning("Moved an invalid news update job to the failed queue.");
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var game = await database.Games.FirstOrDefaultAsync(game => game.Id == gameId && game.SteamAppId != null &&
                database.LibraryEntries.Any(entry => entry.GameId == game.Id && entry.FollowNews), cancellationToken);
            if (game is not null)
            {
                await scope.ServiceProvider.GetRequiredService<SteamNewsClient>().RefreshAsync(game, database, cancellationToken);
                logger.LogInformation("Processed news update for game {GameId}.", gameId);
            }
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (message.DequeueCount >= 3)
            {
                await failed.SendMessageAsync(message.Body, cancellationToken: cancellationToken);
                await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                logger.LogWarning(exception, "Moved news update for game {GameId} to the failed queue after three attempts.", gameId);
            }
            else
            {
                var seconds = Math.Clamp(configuration.GetValue("NewsUpdates:RetryDelaySeconds", 10), 0, 300) * message.DequeueCount;
                await queue.UpdateMessageAsync(message.MessageId, message.PopReceipt, message.Body, TimeSpan.FromSeconds(seconds), cancellationToken);
                logger.LogWarning(exception, "News update for game {GameId} failed on attempt {Attempt}. It will be retried.", gameId, message.DequeueCount);
            }
        }
        return true;
    }
}
