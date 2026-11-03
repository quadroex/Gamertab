using System.Security.Claims;
using Azure;
using Gamertab.Data;
using Gamertab.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Google;

namespace Gamertab.Controllers.Api;

[Authorize, ApiController, Route("api/collections/{id:int}/cover")]
public class CollectionCoversController(AppDbContext database, CollectionCoverService covers) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet(Name = "CollectionCover")]
    public async Task<IActionResult> Image(int id)
    {
        Response.Headers.CacheControl = "no-store";
        var collection = await database.Collections.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection?.CoverBlobName is null)
            return NotFound();
        if (!covers.IsConfigured)
            return StatusCode(503, new { error = "Cover storage is temporarily unavailable." });
        try
        {
            var image = await covers.OpenAsync(collection.CoverBlobName, HttpContext.RequestAborted);
            if (image is null)
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(image, collection.CoverBlobName.EndsWith(".png") ? "image/png" : "image/jpeg");
        }
        catch (Exception exception) when (exception is RequestFailedException or GoogleApiException or HttpRequestException)
        {
            return StatusCode(503, new { error = "Cover storage is temporarily unavailable." });
        }
    }

    [HttpPost(Name = "UploadCollectionCover"), RequestSizeLimit(CollectionCoverService.MaxBytes + 65536)]
    public async Task<IActionResult> Upload(int id, IFormFile cover, bool returnToEdit = false)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        if (!covers.IsConfigured)
            return Reply(id, returnToEdit, 503, "Cover storage is temporarily unavailable.");
        try
        {
            await covers.UploadAsync(collection, cover, HttpContext.RequestAborted);
            return Reply(id, returnToEdit, 201, "Collection cover uploaded.");
        }
        catch (ArgumentException exception) { return Reply(id, returnToEdit, 400, exception.Message); }
        catch (InvalidOperationException exception) { return Reply(id, returnToEdit, 409, exception.Message); }
        catch (Exception exception) when (exception is RequestFailedException or GoogleApiException or HttpRequestException)
        {
            return Reply(id, returnToEdit, 503, "Cover storage is temporarily unavailable. Please try again later.");
        }
    }

    [HttpDelete(Name = "DeleteCollectionCover")]
    [HttpPost("remove", Name = "RemoveCollectionCover")]
    public async Task<IActionResult> Remove(int id, bool returnToEdit = false)
    {
        var collection = await database.Collections.FirstOrDefaultAsync(item => item.Id == id && item.UserId == UserId);
        if (collection is null)
            return NotFound();
        try
        {
            await covers.RemoveAsync(collection);
            return Reply(id, returnToEdit, 204, "Uploaded cover removed.");
        }
        catch (InvalidOperationException exception) { return Reply(id, returnToEdit, 409, exception.Message); }
    }

    private IActionResult Reply(int id, bool returnToEdit, int status, string message)
    {
        if (returnToEdit)
        {
            TempData["Message"] = message;
            return RedirectToAction("Edit", "Collections", new { id });
        }
        if (status == 204)
            return NoContent();
        return StatusCode(status, status == 201 ? new { uploadedCoverUrl = Url.RouteUrl("CollectionCover", new { id }) } : (object)new { error = message });
    }
}
