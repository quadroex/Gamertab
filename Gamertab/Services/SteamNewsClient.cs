using System.Text.Json;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Services;

public class SteamNewsClient(HttpClient http, IMemoryCache cache)
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public async Task RefreshAsync(Game game, AppDbContext database, CancellationToken cancellationToken = default)
    {
        await RefreshGate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(game, database, cancellationToken);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(Game game, AppDbContext database, CancellationToken cancellationToken)
    {
        if (game.SteamAppId is null || cache.TryGetValue("steam-news:" + game.Id, out _))
            return;

        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={game.SteamAppId}&count=12&maxlength=1&feeds=steam_community_announcements";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var existingIds = await database.NewsItems.Where(item => item.GameId == game.Id).Select(item => item.SourceId).ToListAsync(cancellationToken);
        foreach (var item in document.RootElement.GetProperty("appnews").GetProperty("newsitems").EnumerateArray())
        {
            var sourceId = item.GetProperty("gid").GetString()!;
            var articleUrl = item.GetProperty("url").GetString() ?? "";
            if (existingIds.Contains(sourceId) || !Uri.TryCreate(articleUrl, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme != "https")
                continue;
            database.NewsItems.Add(new NewsItem
            {
                GameId = game.Id,
                SourceId = sourceId,
                Title = item.GetProperty("title").GetString() ?? "Untitled update",
                Url = articleUrl,
                Source = item.GetProperty("feedlabel").GetString() ?? "Steam",
                PublishedAt = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("date").GetInt64())
            });
            existingIds.Add(sourceId);
        }
        await database.SaveChangesAsync(cancellationToken);
        cache.Set("steam-news:" + game.Id, true, TimeSpan.FromMinutes(10));
    }
}
