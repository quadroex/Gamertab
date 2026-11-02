using System.Security.Claims;
using System.Text.Json;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Gamertab.Controllers;

[Authorize, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AssistantController(GeminiAgent agent) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(agent.IsConfigured);

    [HttpPost("/api/assistant/chat"), EnableRateLimiting("assistant"), RequestSizeLimit(100000)]
    public async Task Chat([FromBody] AssistantInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { Response.StatusCode = 400; return; }
        if (!agent.IsConfigured) { Response.StatusCode = 503; return; }
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        async Task Emit(string type, object data)
        {
            await Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await agent.RunAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, input, Emit, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await Emit("error", new { message = "The response timed out. Try a shorter request." }); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (InvalidOperationException error) { await Emit("error", new { message = error.Message }); }
        catch (Exception) { if (!cancellationToken.IsCancellationRequested) await Emit("error", new { message = "The assistant could not finish. Please try again." }); }
    }
}
