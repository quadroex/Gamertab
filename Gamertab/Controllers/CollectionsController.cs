using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

[Authorize]
public class CollectionsController(AppDbContext database, GameCatalog catalog, CollectionCoverService covers) : Controller
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
        PrepareCoverForm(collection);
        return View(new CollectionInput { Name = collection.Name, Description = collection.Description, CoverUrl = collection.CoverUrl });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CollectionInput input)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        if (!ModelState.IsValid)
        {
            PrepareCoverForm(collection);
            return View(input);
        }
        input.ApplyTo(collection);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var collection = await database.Collections.Include(item => item.Items).ThenInclude(item => item.Game).FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        return View(collection);
    }

    [HttpGet]
    public async Task<IActionResult> GameOptions(int id, string? term, int page = 1)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await database.Collections.AnyAsync(item => item.Id == id && item.UserId == UserId))
            return NotFound();
        term = term?.Trim();
        if (page < 1 || page > 10000 || term?.Length > 100)
            return BadRequest();
        if (string.IsNullOrEmpty(term) || term.Length < 3)
            return Ok(new { results = Array.Empty<object>(), pagination = new { more = false } });

        var matches = await catalog.BrowseAsync(term, null, page, HttpContext.RequestAborted);
        var selectedIds = await database.CollectionItems.Where(item => item.CollectionId == id).Select(item => item.GameId).ToListAsync();
        var results = matches.Games.Where(game => !selectedIds.Contains(game.Id)).Select(game => new
        {
            id = game.Id > 0 ? $"saved:{game.Id}" : $"igdb:{game.IgdbId}",
            text = game.Name
        });
        return Ok(new { results, pagination = new { more = matches.HasNextPage }, notice = matches.Notice });
    }

    [HttpPost]
    public async Task<IActionResult> AddGame(int id, string? gameKey)
    {
        if (!await database.Collections.AnyAsync(item => item.Id == id && item.UserId == UserId))
            return NotFound();
        var parts = gameKey?.Split(':');
        if (parts?.Length != 2 || !int.TryParse(parts[1], out var selectedId) || selectedId <= 0 || parts[0] is not ("saved" or "igdb"))
            return BadRequest();

        Game? game;
        try
        {
            game = parts[0] == "saved" ? await database.Games.FindAsync(selectedId) :
                await catalog.ImportAsync(selectedId, HttpContext.RequestAborted);
        }
        catch (HttpRequestException)
        {
            TempData["Message"] = "Game details are temporarily unavailable. Please try again later.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Message"] = "Game details took too long to load. Please try again later.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (game is null)
            return NotFound();
        var gameId = game.Id;
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
        try { await covers.DeleteCollectionAsync(collection); }
        catch (InvalidOperationException exception)
        {
            TempData["Message"] = exception.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        return RedirectToAction(nameof(Index));
    }

    private void PrepareCoverForm(GameCollection collection)
    {
        ViewData["CanUploadCover"] = covers.IsConfigured;
        if (collection.CoverBlobName is not null)
            ViewData["UploadedCoverUrl"] = Url.RouteUrl("CollectionCover", new { id = collection.Id });
    }
}
