using System.ComponentModel.DataAnnotations;
using Gamertab.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers.Api;

[ApiController, Route("api/games")]
public class GamesApiController(AppDbContext database) : ControllerBase
{
    [HttpGet(Name = "GamesList")]
    public async Task<IActionResult> List([Range(0, int.MaxValue)] int skip = 0, [Range(1, 100)] int limit = 12, [StringLength(100)] string? q = null)
    {
        var query = database.Games.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(game => game.Name.ToLower().Contains(q.Trim().ToLower()));
        var total = await query.CountAsync();
        var items = await query.OrderBy(game => game.Name).ThenBy(game => game.Id).Skip(skip).Take(limit).ToListAsync();
        var nextLink = (long)skip + limit < total ? Url.Link("GamesList", new { skip = skip + limit, limit, q }) : null;
        return Ok(new { items, total, skip, limit, nextLink });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var game = await database.Games.AsNoTracking().FirstOrDefaultAsync(game => game.Id == id);
        return game is null ? NotFound() : Ok(game);
    }
}
