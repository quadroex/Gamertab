using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

public class DiscoverController(AppDbContext database, IgdbClient igdb) : Controller
{
    public async Task<IActionResult> Index(string? q)
    {
        ViewData["Query"] = q;
        ViewData["Configured"] = igdb.IsConfigured;
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2 || q.Length > 100 || !igdb.IsConfigured)
            return View(new List<Game>());
        try
        {
            return View(await igdb.SearchAsync(q, HttpContext.RequestAborted));
        }
        catch (HttpRequestException)
        {
            ViewData["Notice"] = "IGDB is temporarily unavailable. Please try again later.";
            return View(new List<Game>());
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            ViewData["Notice"] = "The search took too long to respond. Please try again.";
            return View(new List<Game>());
        }
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Import(int igdbId)
    {
        if (igdbId <= 0 || !igdb.IsConfigured)
            return BadRequest();
        try
        {
            var imported = await igdb.GetAsync(igdbId, HttpContext.RequestAborted);
            if (imported is null)
                return NotFound();
            var existing = await database.Games.FirstOrDefaultAsync(game => game.IgdbId == igdbId || (imported.SteamAppId != null && game.SteamAppId == imported.SteamAppId));
            if (existing is null)
                database.Games.Add(imported);
            else
            {
                imported.Id = existing.Id;
                database.Entry(existing).CurrentValues.SetValues(imported);
            }
            await database.SaveChangesAsync();
            return RedirectToAction("Details", "Games", new { id = imported.Id });
        }
        catch (HttpRequestException)
        {
            TempData["Message"] = "The game could not be imported. Please try again later.";
            return RedirectToAction(nameof(Index));
        }
    }
}
