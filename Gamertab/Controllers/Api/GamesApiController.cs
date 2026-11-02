using System.ComponentModel.DataAnnotations;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers.Api;

[ApiController, Route("api/games")]
[OutputCache(Duration = 60, VaryByQueryKeys = ["skip", "limit", "q"], Tags = [GameCatalog.CacheTag])]
public class GamesApiController(AppDbContext database, GameSearchIndex searchIndex) : ControllerBase
{
    [HttpGet(Name = "GamesList")]
    public async Task<IActionResult> List([Range(0, int.MaxValue)] int skip = 0, [Range(1, 100)] int limit = 12, [StringLength(100)] string? q = null)
    {
        var query = database.Games.AsNoTracking();
        int total;
        List<Game> items;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var matches = searchIndex.Search(q, skip, limit);
            var found = await query.Where(game => matches.Ids.Contains(game.Id)).ToListAsync();
            items = found.OrderBy(game => Array.IndexOf(matches.Ids, game.Id)).ToList();
            total = matches.Total;
        }
        else
        {
            total = await query.CountAsync();
            items = await query.OrderBy(game => game.Name).ThenBy(game => game.Id).Skip(skip).Take(limit).ToListAsync();
        }
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
