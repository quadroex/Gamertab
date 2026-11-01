using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

public class GamesController(AppDbContext database, IgdbClient igdb, SteamNewsClient news, GameCatalog catalog) : Controller
{
    public async Task<IActionResult> Index(string? q, string? genre, int page = 1)
    {
        q = q?.Trim();
        if (page < 1 || page > 10000 || q?.Length > 100 ||
            (!string.IsNullOrEmpty(genre) && !CatalogPage.Genres.ContainsKey(genre)))
            return BadRequest();
        if (q?.Length == 1)
            return View(new CatalogPage([], q, genre, page, false, false, "Enter at least two characters to search."));
        return View(await catalog.BrowseAsync(q, genre, page, HttpContext.RequestAborted));
    }

    public async Task<IActionResult> Open(int id)
    {
        if (id <= 0)
            return BadRequest();
        try
        {
            var game = await catalog.ImportAsync(id, HttpContext.RequestAborted);
            return game is null ? NotFound() : RedirectToAction(nameof(Details), new { id = game.Id });
        }
        catch (HttpRequestException)
        {
            TempData["Message"] = "Game details are temporarily unavailable. Please try again later.";
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Message"] = "Game details took too long to load. Please try again later.";
        }
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id)
    {
        var game = await database.Games.FindAsync(id);
        if (game is null)
            return NotFound();
        if (game.IgdbId.HasValue && game.DataSource != "IGDB")
        {
            try
            {
                game = await catalog.ImportAsync(game.IgdbId.Value, HttpContext.RequestAborted) ?? game;
            }
            catch (HttpRequestException)
            {
                ViewData["DetailsNotice"] = "Live details are temporarily unavailable. Showing saved information.";
            }
            catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                ViewData["DetailsNotice"] = "Live details took too long to load. Showing saved information.";
            }
        }
        try
        {
            await news.RefreshAsync(game, database, HttpContext.RequestAborted);
        }
        catch (HttpRequestException)
        {
            ViewData["NewsNotice"] = "Live news is temporarily unavailable. Previously saved news is shown below.";
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            ViewData["NewsNotice"] = "The news service took too long to respond. Please try again later.";
        }
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var entry = userId is null ? null : await database.LibraryEntries.FirstOrDefaultAsync(item => item.UserId == userId && item.GameId == id);
        var articles = await database.NewsItems.Where(item => item.GameId == id).OrderByDescending(item => item.PublishedAt).Take(12).ToListAsync();
        return View(new GamePage(game, entry, articles, igdb.IsConfigured));
    }

    [Route("Home/Error")]
    public IActionResult Error() => View("~/Views/Shared/Error.cshtml", new ErrorViewModel { RequestId = HttpContext.TraceIdentifier });
}
