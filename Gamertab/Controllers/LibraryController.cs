using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

[Authorize]
public class LibraryController(AppDbContext database, RecommendationService recommendations) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index(string? status)
    {
        var query = database.LibraryEntries.Include(entry => entry.Game).Where(entry => entry.UserId == UserId);
        if (Enum.TryParse<PlayStatus>(status, out var parsedStatus) && Enum.IsDefined(parsedStatus))
            query = query.Where(entry => entry.Status == parsedStatus);
        return View(new LibraryPage(await query.OrderByDescending(entry => entry.CompletedOn).ThenBy(entry => entry.Game.Name).ToListAsync(), status));
    }

    [HttpPost]
    public async Task<IActionResult> Add(int gameId)
    {
        if (!await database.Games.AnyAsync(game => game.Id == gameId))
            return NotFound();
        var entry = await database.LibraryEntries.FirstOrDefaultAsync(item => item.UserId == UserId && item.GameId == gameId);
        if (entry is null)
        {
            entry = new LibraryEntry { UserId = UserId, GameId = gameId };
            database.LibraryEntries.Add(entry);
            await database.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Edit), new { id = entry.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var entry = await database.LibraryEntries.Include(item => item.Game).FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (entry is null)
            return NotFound();
        return View(new LibraryEditPage(entry.Game, new LibraryInput
        {
            GameId = entry.GameId,
            Status = entry.Status,
            PersonalRating = entry.PersonalRating,
            Notes = entry.Notes,
            StartedOn = entry.StartedOn,
            CompletedOn = entry.CompletedOn,
            FollowNews = entry.FollowNews
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, [Bind(Prefix = "Input")] LibraryInput input)
    {
        var entry = await database.LibraryEntries.Include(item => item.Game).FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (entry is null)
            return NotFound();
        if (input.GameId != entry.GameId)
            return BadRequest();
        if (!ModelState.IsValid)
            return View(new LibraryEditPage(entry.Game, input));
        input.ApplyTo(entry);
        await database.SaveChangesAsync();
        TempData["Message"] = "Your library has been updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var entry = await database.LibraryEntries.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (entry is null)
            return NotFound();
        database.LibraryEntries.Remove(entry);
        await database.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Recommendations() => View(await recommendations.GetAsync(UserId, HttpContext.RequestAborted));
}
