using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

public class GamesController(AppDbContext database, IgdbClient igdb, SteamNewsClient news) : Controller
{
    public async Task<IActionResult> Index(string? q, string? genre, int page = 1)
    {
        const int pageSize = 12;
        page = Math.Max(1, page);
        var query = database.Games.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(game => game.Name.ToLower().Contains(q.Trim().ToLower()));
        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(game => game.Genres.ToLower().Contains(genre.ToLower()));
        var total = await query.CountAsync();
        var games = await query.OrderBy(game => game.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return View(new CatalogPage(games, q, genre, page, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))));
    }

    public async Task<IActionResult> Details(int id)
    {
        var game = await database.Games.FindAsync(id);
        if (game is null)
            return NotFound();
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
