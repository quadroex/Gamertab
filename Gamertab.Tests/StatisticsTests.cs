using System.Security.Claims;
using Gamertab.Controllers;
using Gamertab.Data;
using Gamertab.Models;
using Gamertab.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class StatisticsTests
{
    [Fact]
    public async Task StatisticsRespectOwnerMonthBoundariesMissingDatesAndUnratedGames()
    {
        using var factory = new AppFactory();
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Users.AddRange(new IdentityUser { Id = "owner", UserName = "owner" }, new IdentityUser { Id = "other", UserName = "other" });
        database.UserPlans.Add(new UserPlan { UserId = "owner", Plan = AccountPlan.Premium });
        var games = await database.Games.OrderBy(game => game.Id).ToListAsync();
        games[0].Genres = "Puzzle,puzzle,Adventure";
        games[1].Genres = "Puzzle,Role-playing";
        foreach (var game in games.Skip(2))
            game.Genres = "";
        games[5].Genres = "Private genre";
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        database.LibraryEntries.AddRange(
            new LibraryEntry { UserId = "owner", GameId = games[0].Id, Status = PlayStatus.Completed, PersonalRating = 0, CompletedOn = today },
            new LibraryEntry { UserId = "owner", GameId = games[1].Id, Status = PlayStatus.Completed, PersonalRating = 10, CompletedOn = firstMonth },
            new LibraryEntry { UserId = "owner", GameId = games[2].Id, Status = PlayStatus.Completed, CompletedOn = firstMonth.AddDays(-1) },
            new LibraryEntry { UserId = "owner", GameId = games[3].Id, Status = PlayStatus.Planned },
            new LibraryEntry { UserId = "owner", GameId = games[4].Id, Status = PlayStatus.Completed },
            new LibraryEntry { UserId = "other", GameId = games[5].Id, Status = PlayStatus.Completed, PersonalRating = 10, CompletedOn = today });
        await database.SaveChangesAsync();
        var controller = new LibraryController(database, scope.ServiceProvider.GetRequiredService<RecommendationService>(), scope.ServiceProvider.GetRequiredService<PlanAccess>(),
            scope.ServiceProvider.GetRequiredService<LibraryExportClient>(), scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LibraryController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Test")) }
            }
        };
        var view = Assert.IsType<ViewResult>(await controller.Statistics());
        var statistics = Assert.IsType<LibraryStatisticsPage>(view.Model);
        Assert.Equal(5, statistics.Total);
        Assert.Equal(4, statistics.Completed);
        Assert.Equal(5, statistics.AverageRating);
        Assert.Equal(2, statistics.Rated);
        Assert.Equal(1, statistics.UndatedCompletions);
        Assert.Equal(4, statistics.Statuses.Single(value => value.Label == "Completed").Count);
        Assert.Equal(2, statistics.Genres.Single(value => value.Label == "Puzzle").Count);
        Assert.DoesNotContain(statistics.Genres, value => value.Label == "Private genre");
        Assert.Equal(12, statistics.Months.Count);
        Assert.Equal(2, statistics.Months.Sum(value => value.Count));
        Assert.Equal(1, statistics.Months[0].Count);
        Assert.Equal(1, statistics.Months[^1].Count);
    }
}
