using System.Security.Claims;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class FeedbackController(AppDbContext database, HubSpotClient crm, ILogger<FeedbackController> logger) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private async Task PrepareAsync(FeedbackInput input)
    {
        ViewData["FeedbackAvailable"] = crm.IsConfigured;
        ViewData["Requests"] = await database.FeedbackRequests.AsNoTracking().Where(request => request.UserId == UserId).OrderByDescending(request => request.Id).Take(20).ToListAsync();
        if (input.GameId.HasValue) ViewData["GameName"] = await database.Games.Where(game => game.Id == input.GameId).Select(game => game.Name).FirstOrDefaultAsync();
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? gameId)
    {
        var input = new FeedbackInput { GameId = gameId };
        await PrepareAsync(input);
        if (gameId.HasValue && ViewData["GameName"] is null) return NotFound();
        return View(input);
    }

    [HttpPost, EnableRateLimiting("feedback")]
    public async Task<IActionResult> Send(FeedbackInput input)
    {
        await PrepareAsync(input);
        if (input.GameId.HasValue && ViewData["GameName"] is null) ModelState.AddModelError(nameof(input.GameId), "Game not found.");
        if (!ModelState.IsValid) return View("Index", input);
        try
        {
            var ticketId = await crm.CreateAsync(input, HttpContext.RequestAborted);
            var request = new FeedbackRequest { UserId = UserId, Subject = input.Subject.Trim(), Category = input.Category, TicketId = ticketId };
            database.FeedbackRequests.Add(request);
            await database.SaveChangesAsync();
            TempData["Message"] = "Your feedback was sent. You can check its current status here.";
            return RedirectToAction(nameof(Details), new { id = request.Id });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Feedback CRM request failed. No automatic retry was made.");
            ModelState.AddModelError("", "The feedback service did not confirm delivery. Your text is still here. Please check your recent requests before sending again.");
            return View("Index", input);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var request = await database.FeedbackRequests.AsNoTracking().FirstOrDefaultAsync(request => request.Id == id && request.UserId == UserId);
        if (request is null) return NotFound();
        try
        {
            var ticket = await crm.ReadAsync(request.TicketId, HttpContext.RequestAborted);
            return View(new FeedbackTicketPage(request, ticket.Status, ticket.Message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return View(new FeedbackTicketPage(request, null, null));
        }
    }
}
