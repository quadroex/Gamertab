using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Cloud.Functions.Framework;
using Microsoft.AspNetCore.Http;

namespace Gamertab.LibraryExport;

public record ExportGame(int GameId, string Name, string Status, double? Rating, DateOnly? StartedOn, DateOnly? CompletedOn);

public class Function : IHttpFunction
{
    public static readonly string[] Statuses = ["Planned", "Playing", "Completed", "OnHold", "Dropped"];

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Method != "POST")
        {
            context.Response.StatusCode = 405;
            context.Response.Headers.Allow = "POST";
            return;
        }
        try
        {
            context.Request.EnableBuffering(32768, 2_000_000);
            var games = await context.Request.ReadFromJsonAsync<List<ExportGame>>(cancellationToken: context.RequestAborted);
            if (games is null || games.Count > 10000 || games.Any(game => game is null || game.GameId <= 0 || string.IsNullOrWhiteSpace(game.Name) ||
                game.Name.Length > 200 || !Statuses.Contains(game.Status) || game.Rating is < 0 or > 10 || game.Rating.HasValue && !double.IsFinite(game.Rating.Value)))
            {
                context.Response.StatusCode = 400;
                return;
            }
            context.Response.ContentType = "text/markdown; charset=utf-8";
            context.Response.Headers.ContentDisposition = "attachment; filename=gamertab-library.md";
            var siteUrl = Environment.GetEnvironmentVariable("SITE_BASE_URL") ?? "https://gamertab-917646089327.us-east1.run.app";
            await context.Response.WriteAsync(Render(games, siteUrl), context.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or IOException or BadHttpRequestException or InvalidOperationException)
        {
            context.Response.StatusCode = 400;
        }
    }

    public static string Render(IReadOnlyList<ExportGame> games, string siteUrl)
    {
        var result = new StringBuilder("# My game library\n\n");
        if (games.Count == 0) result.Append("Your library is empty.\n");
        foreach (var status in Statuses)
        {
            var entries = games.Where(game => game.Status == status).OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0) continue;
            result.Append("## ").Append(status == "OnHold" ? "On hold" : status).Append("\n\n");
            foreach (var game in entries)
            {
                var name = Regex.Replace(Regex.Replace(game.Name, @"\s+", " ").Trim(), @"([\\`*_{}\[\]<>()#+.!|~-])", @"\$1");
                result.Append("- [").Append(name).Append("](").Append(siteUrl.TrimEnd('/')).Append("/Games/Details/").Append(game.GameId).Append(')');
                if (game.Rating.HasValue) result.Append(" - Rating ").Append(game.Rating.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append("/10");
                if (game.StartedOn.HasValue) result.Append(" - Started ").Append(game.StartedOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (game.CompletedOn.HasValue) result.Append(" - Finished ").Append(game.CompletedOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                result.Append('\n');
            }
            result.Append('\n');
        }
        return result.ToString();
    }
}
