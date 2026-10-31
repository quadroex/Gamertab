using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers.Api;

[Authorize, ApiController, Route("api/collections")]
public class CollectionsApiController(AppDbContext database) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await database.Collections.Where(collection => collection.UserId == UserId)
        .Select(collection => new { collection.Id, collection.Name, collection.Description, collection.CoverUrl, count = collection.Items.Count }).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var collection = await database.Collections.Where(collection => collection.Id == id && collection.UserId == UserId)
            .Select(collection => new { collection.Id, collection.Name, collection.Description, collection.CoverUrl, games = collection.Items.Select(item => new { item.GameId, item.Game.Name }) }).FirstOrDefaultAsync();
        return collection is null ? NotFound() : Ok(collection);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CollectionInput input)
    {
        var collection = new GameCollection { UserId = UserId };
        input.ApplyTo(collection);
        database.Collections.Add(collection);
        await database.SaveChangesAsync();
        return CreatedAtAction(nameof(Details), new { id = collection.Id }, new { collection.Id, collection.Name });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CollectionInput input)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(collection => collection.Id == id && collection.UserId == UserId);
        if (collection is null)
            return NotFound();
        input.ApplyTo(collection);
        await database.SaveChangesAsync();
        return Ok(new { collection.Id, collection.Name });
    }

    [HttpPost("{id:int}/games")]
    public async Task<IActionResult> AddGame(int id, CollectionGameInput input)
    {
        if (!await database.Collections.AnyAsync(collection => collection.Id == id && collection.UserId == UserId) ||
            !await database.Games.AnyAsync(game => game.Id == input.GameId))
            return NotFound();
        if (await database.CollectionItems.AnyAsync(item => item.CollectionId == id && item.GameId == input.GameId))
            return Conflict(new { error = "This game is already in the collection." });
        database.CollectionItems.Add(new CollectionItem { CollectionId = id, GameId = input.GameId });
        await database.SaveChangesAsync();
        return StatusCode(201, new { collectionId = id, input.GameId });
    }

    [HttpDelete("{id:int}/games/{gameId:int}")]
    public async Task<IActionResult> RemoveGame(int id, int gameId)
    {
        var item = await database.CollectionItems.Include(item => item.Collection).FirstOrDefaultAsync(item => item.CollectionId == id && item.GameId == gameId && item.Collection.UserId == UserId);
        if (item is null)
            return NotFound();
        database.CollectionItems.Remove(item);
        await database.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(collection => collection.Id == id && collection.UserId == UserId);
        if (collection is null)
            return NotFound();
        database.Collections.Remove(collection);
        await database.SaveChangesAsync();
        return NoContent();
    }
}
