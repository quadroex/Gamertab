using System.Xml.Linq;
using Gamertab.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Controllers;

public class SeoController(AppDbContext database, IConfiguration configuration) : Controller
{
    private string BaseUrl => (configuration["Site:BaseUrl"] ?? "https://gamertab-917646089327.us-east1.run.app").TrimEnd('/');

    [HttpGet("robots.txt")]
    public IActionResult Robots() => Content("User-agent: *\nDisallow: /Account/\nDisallow: /Library/\nDisallow: /Collections/\nDisallow: /Assistant/\nDisallow: /api/\nDisallow: /hubs/\nSitemap: " + BaseUrl + "/sitemap.xml\n", "text/plain");

    [HttpGet("sitemap.xml")]
    public async Task<IActionResult> Sitemap()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var ids = await database.Games.AsNoTracking().OrderBy(game => game.Id).Select(game => game.Id).Take(49999).ToListAsync();
        var paths = new[] { "/" }.Concat(ids.Select(id => $"/Games/Details/{id}"));
        var document = new XDocument(new XElement(ns + "urlset", paths.Select(path => new XElement(ns + "url", new XElement(ns + "loc", BaseUrl + path)))));
        return Content(document.ToString(), "application/xml; charset=utf-8");
    }
}
