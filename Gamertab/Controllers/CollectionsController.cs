using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

[Authorize]
public class CollectionsController(AppDbContext database) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index() => View(await database.Collections.Include(collection => collection.Items).Where(collection => collection.UserId == UserId).OrderBy(collection => collection.Name).ToListAsync());

    [HttpGet]
    public IActionResult Create() => View("Edit", new CollectionInput());

    [HttpPost]
    public async Task<IActionResult> Create(CollectionInput input)
    {
        if (!ModelState.IsValid)
            return View("Edit", input);
        var collection = new GameCollection { UserId = UserId };
        input.ApplyTo(collection);
        database.Collections.Add(collection);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id = collection.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        return View(new CollectionInput { Name = collection.Name, Description = collection.Description, CoverUrl = collection.CoverUrl });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CollectionInput input)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        if (!ModelState.IsValid)
            return View(input);
        input.ApplyTo(collection);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var collection = await database.Collections.Include(item => item.Items).ThenInclude(item => item.Game).FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        var selectedIds = collection.Items.Select(item => item.GameId).ToList();
        var available = await database.Games.Where(game => !selectedIds.Contains(game.Id)).OrderBy(game => game.Name).ToListAsync();
        return View(new CollectionPage(collection, available));
    }

    [HttpPost]
    public async Task<IActionResult> AddGame(int id, int gameId)
    {
        if (!await database.Collections.AnyAsync(item => item.Id == id && item.UserId == UserId) || !await database.Games.AnyAsync(game => game.Id == gameId))
            return NotFound();
        if (!await database.CollectionItems.AnyAsync(item => item.CollectionId == id && item.GameId == gameId))
        {
            database.CollectionItems.Add(new CollectionItem { CollectionId = id, GameId = gameId });
            await database.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> RemoveGame(int id, int gameId)
    {
        var item = await database.CollectionItems.Include(item => item.Collection).FirstOrDefaultAsync(item => item.CollectionId == id && item.GameId == gameId && item.Collection.UserId == UserId);
        if (item is null)
            return NotFound();
        database.CollectionItems.Remove(item);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        database.Collections.Remove(collection);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
