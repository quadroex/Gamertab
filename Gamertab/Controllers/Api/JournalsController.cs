using System.Security.Claims;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Gamertab.Controllers.Api;

[Authorize, ApiController, Route("api/journals/{gameId:int}"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class JournalsController(JournalStore journals, IHubContext<JournalHub> hub) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> Read(int gameId) => await journals.CanReadAsync(UserId, gameId)
        ? Ok(await journals.ReadAsync(UserId, gameId)) : NotFound();

    [HttpPut, RequestSizeLimit(8000000)]
    public async Task<IActionResult> Save(int gameId, JournalInput input)
    {
        if (!await journals.CanReadAsync(UserId, gameId)) return NotFound();
        if (!await journals.SaveAsync(UserId, gameId, input))
            return Conflict(await journals.ReadAsync(UserId, gameId));
        await hub.Clients.Group(JournalHub.GroupName(UserId, gameId)).SendAsync("JournalChanged", input.Revision + 1, input.ClientId);
        return Ok(new JournalSnapshot(input.Revision + 1, input.Blocks, UserId));
    }
}
