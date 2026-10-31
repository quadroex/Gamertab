using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Data;

public static class StarterData
{
    public static async Task SeedAsync(AppDbContext database)
    {
        if (await database.Games.AnyAsync())
            return;

        database.Games.AddRange(
            Create(72, 620, "Portal 2", "A puzzle adventure built around portals, physics and cooperative challenges.", "Platform,Puzzle,Adventure", "Science fiction", "Single player,Co-operative"),
            Create(1877, 1091500, "Cyberpunk 2077", "An open-world role-playing adventure set in the futuristic Night City.", "Shooter,Role-playing (RPG),Adventure", "Science fiction", "Single player"),
            Create(18866, 381210, "Dead by Daylight", "An asymmetric horror game with a killer and a team of survivors.", "Action,Horror", "Horror,Survival", "Multiplayer,Co-operative"),
            Create(null, 1145360, "Hades", "An action roguelike about finding a way out of the Underworld.", "Action,Role-playing (RPG)", "Fantasy", "Single player"),
            Create(null, 413150, "Stardew Valley", "Build a farm, explore the countryside and get to know a small town.", "Simulator,Role-playing (RPG)", "Sandbox", "Single player,Co-operative"),
            Create(null, 548430, "Deep Rock Galactic", "A cooperative shooter about exploring dangerous caves with a team of space dwarves.", "Action,Shooter", "Science fiction", "Single player,Co-operative"));
        await database.SaveChangesAsync();
    }

    private static Game Create(int? igdbId, int steamId, string name, string summary, string genres, string themes, string modes) => new()
    {
        IgdbId = igdbId,
        SteamAppId = steamId,
        Name = name,
        Summary = summary,
        Genres = genres,
        Themes = themes,
        Modes = modes,
        Platforms = "PC (Microsoft Windows)",
        CoverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{steamId}/header.jpg"
    };
}
