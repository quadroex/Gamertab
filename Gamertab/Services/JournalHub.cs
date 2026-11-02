using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gamertab.Services;

[Authorize]
public class JournalHub(JournalStore journals) : Hub
{
    public static string GroupName(string userId, int gameId) => $"journal:{userId}:{gameId}";

    public async Task Subscribe(int gameId)
    {
        if (Context.UserIdentifier is null || !await journals.CanReadAsync(Context.UserIdentifier, gameId))
            throw new HubException("Journal not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(Context.UserIdentifier, gameId));
    }
}
