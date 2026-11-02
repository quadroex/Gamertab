using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

[Authorize]
public class LibraryController(AppDbContext database, RecommendationService recommendations, PlanAccess plans) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index(string? status)
    {
        var query = database.LibraryEntries.Include(entry => entry.Game).Where(entry => entry.UserId == UserId);
        if (Enum.TryParse<PlayStatus>(status, out var parsedStatus) && Enum.IsDefined(parsedStatus))
            query = query.Where(entry => entry.Status == parsedStatus);
        return View(new LibraryPage(await query.OrderByDescending(entry => entry.CompletedOn).ThenBy(entry => entry.Game.Name).ToListAsync(), status));
    }

    public async Task<IActionResult> Statistics()
    {
        Response.Headers.CacheControl = "no-store";
        var access = await plans.GetAsync(UserId);
        if (!access.CanViewStatistics)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return View("PremiumRequired", access);
        }
        var entries = await database.LibraryEntries.AsNoTracking().Where(entry => entry.UserId == UserId)
            .Select(entry => new { entry.Status, entry.PersonalRating, entry.CompletedOn, entry.Game.Genres }).ToListAsync();
        var statuses = Enum.GetValues<PlayStatus>().Select(status => new StatisticValue(
            status == PlayStatus.OnHold ? "On hold" : status.ToString(), entries.Count(entry => entry.Status == status))).ToList();
        var genres = entries.SelectMany(entry => entry.Genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StatisticValue(group.Key, group.Count())).OrderByDescending(value => value.Count)
            .ThenBy(value => value.Label).Take(6).ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var months = Enumerable.Range(0, 12).Select(index => firstMonth.AddMonths(index))
            .Select(month => new StatisticValue(month.ToString("MMM yyyy"), entries.Count(entry => entry.Status == PlayStatus.Completed &&
                entry.CompletedOn >= month && entry.CompletedOn < month.AddMonths(1)))).ToList();
        var ratings = entries.Where(entry => entry.PersonalRating.HasValue).Select(entry => entry.PersonalRating!.Value).ToList();
        return View(new LibraryStatisticsPage(entries.Count, entries.Count(entry => entry.Status == PlayStatus.Completed),
            ratings.Count == 0 ? null : ratings.Average(), ratings.Count,
            entries.Count(entry => entry.Status == PlayStatus.Completed && !entry.CompletedOn.HasValue), statuses, genres, months));
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

    [HttpGet]
    public async Task<IActionResult> Journal(int id)
    {
        Response.Headers.CacheControl = "no-store";
        var entry = await database.LibraryEntries.AsNoTracking().Include(item => item.Game)
            .FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (entry is null)
            return NotFound();
        return View(new PlayJournalPage(entry.Id, entry.Game.Name, $"gamertab:journal:v1:{UserId}:{entry.GameId}"));
    }

    public async Task<IActionResult> Recommendations() => View(await recommendations.GetAsync(UserId, HttpContext.RequestAborted));
}
