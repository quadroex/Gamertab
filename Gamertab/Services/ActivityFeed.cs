using Gamertab.Data;
using Gamertab.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

[Authorize]
public class ActivityHub : Hub { }

public class ActivityFeed(AppDbContext database, ActivitySignal signal, IHubContext<ActivityHub> hub, ILogger<ActivityFeed> logger)
{
    public async Task RecordAsync(LibraryEntry entry, string action)
    {
        var name = entry.Game?.Name ?? await database.Games.Where(game => game.Id == entry.GameId).Select(game => game.Name).SingleAsync();
        var activity = new LibraryActivity { UserId = entry.UserId, GameId = entry.GameId, GameName = name, Action = action, Status = entry.Status.ToString() };
        database.LibraryActivities.Add(activity);
        await database.SaveChangesAsync();
        signal.Publish(entry.UserId);
        try { await hub.Clients.User(entry.UserId).SendAsync("LibraryChanged", activity.Id); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("A library update was saved but its live notification could not be delivered.");
        }
    }

    public async Task<ActivityBatch> ReadAsync(string userId, long after, CancellationToken cancellationToken)
    {
        var query = database.LibraryActivities.AsNoTracking().Where(item => item.UserId == userId);
        List<LibraryActivity> items;
        if (after == 0)
            items = (await query.OrderByDescending(item => item.Id).Take(20).ToListAsync(cancellationToken)).OrderBy(item => item.Id).ToList();
        else
            items = await query.Where(item => item.Id > after).OrderBy(item => item.Id).Take(101).ToListAsync(cancellationToken);
        var more = items.Count > 100;
        var events = items.Take(100).Select(item => new ActivityItem(item.Id, item.GameId, item.GameName, item.Action, item.Status, item.CreatedUtc)).ToList();
        return new(events, events.LastOrDefault()?.Id ?? after, more);
    }
}
