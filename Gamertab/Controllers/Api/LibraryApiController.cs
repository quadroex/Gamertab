using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers.Api;

[Authorize, ApiController, Route("api/library")]
public class LibraryApiController(AppDbContext database) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await database.LibraryEntries.Where(entry => entry.UserId == UserId).OrderBy(entry => entry.Id)
        .Select(entry => new { entry.Id, entry.GameId, gameName = entry.Game.Name, entry.Status, entry.PersonalRating, entry.Notes, entry.StartedOn, entry.CompletedOn, entry.FollowNews }).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var entry = await database.LibraryEntries.Where(entry => entry.Id == id && entry.UserId == UserId)
            .Select(entry => new { entry.Id, entry.GameId, gameName = entry.Game.Name, entry.Status, entry.PersonalRating, entry.Notes, entry.StartedOn, entry.CompletedOn, entry.FollowNews }).FirstOrDefaultAsync();
        return entry is null ? NotFound() : Ok(entry);
    }

    [HttpPost]
    public async Task<IActionResult> Create(LibraryInput input)
    {
        if (!await database.Games.AnyAsync(game => game.Id == input.GameId))
            return NotFound(new { error = "Game not found." });
        if (await database.LibraryEntries.AnyAsync(entry => entry.UserId == UserId && entry.GameId == input.GameId))
            return Conflict(new { error = "This game is already in your library." });
        var entry = new LibraryEntry { UserId = UserId, GameId = input.GameId };
        input.ApplyTo(entry);
        database.LibraryEntries.Add(entry);
        await database.SaveChangesAsync();
        return CreatedAtAction(nameof(Details), new { id = entry.Id }, new { entry.Id, entry.GameId, entry.Status });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, LibraryInput input)
    {
        var entry = await database.LibraryEntries.FirstOrDefaultAsync(entry => entry.Id == id && entry.UserId == UserId);
        if (entry is null)
            return NotFound();
        if (input.GameId != entry.GameId)
            return BadRequest(new { error = "The game of an existing entry cannot be changed." });
        input.ApplyTo(entry);
        await database.SaveChangesAsync();
        return Ok(new { entry.Id, entry.GameId, entry.Status });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entry = await database.LibraryEntries.FirstOrDefaultAsync(entry => entry.Id == id && entry.UserId == UserId);
        if (entry is null)
            return NotFound();
        database.LibraryEntries.Remove(entry);
        await database.SaveChangesAsync();
        return NoContent();
    }
}
