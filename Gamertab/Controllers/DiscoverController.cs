using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gamertab.Controllers;

public class DiscoverController(GameCatalog catalog, IgdbClient igdb) : Controller
{
    public IActionResult Index(string? q) => RedirectToAction("Index", "Games", new { q });

    [Authorize, HttpPost]
    public async Task<IActionResult> Import(int igdbId)
    {
        if (igdbId <= 0 || !igdb.IsConfigured)
            return BadRequest();
        try
        {
            var imported = await catalog.ImportAsync(igdbId, HttpContext.RequestAborted, true);
            if (imported is null)
                return NotFound();
            return RedirectToAction("Details", "Games", new { id = imported.Id });
        }
        catch (HttpRequestException)
        {
            TempData["Message"] = "The game could not be imported. Please try again later.";
            return RedirectToAction(nameof(Index));
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Message"] = "The refresh took too long. Please try again later.";
            return RedirectToAction(nameof(Index));
        }
    }
}
