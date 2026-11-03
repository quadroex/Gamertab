using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gamertab.Controllers.Api;

[Authorize, ApiController, Route("api/activity"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ActivityController(ActivityFeed feed, ActivitySignal signal) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> Read([Range(0, long.MaxValue)] long after = 0, [Range(0, 20)] int wait = 0)
    {
        using var watch = signal.Watch(UserId);
        var batch = await feed.ReadAsync(UserId, after, HttpContext.RequestAborted);
        if (batch.Events.Count == 0 && wait > 0)
        {
            await Task.WhenAny(watch.Changed, Task.Delay(TimeSpan.FromSeconds(wait), HttpContext.RequestAborted));
            batch = await feed.ReadAsync(UserId, after, HttpContext.RequestAborted);
        }
        return Ok(batch);
    }

    [Route("socket")]
    public async Task Socket([Range(0, long.MaxValue)] long after = 0)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = 400;
            return;
        }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        var received = socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), stop.Token);
        try
        {
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var watch = signal.Watch(UserId);
                var batch = await feed.ReadAsync(UserId, after, stop.Token);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token);
                after = batch.Cursor;
                if (batch.HasMore) continue;
                var completed = await Task.WhenAny(watch.Changed, received, Task.Delay(TimeSpan.FromSeconds(20), stop.Token));
                if (completed == received) break;
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        finally
        {
            stop.Cancel();
            try { await received; } catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        }
    }
}
