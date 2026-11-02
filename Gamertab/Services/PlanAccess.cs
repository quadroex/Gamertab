using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class PlanAccess(AppDbContext database, IConfiguration configuration, IHostEnvironment environment)
{
    public bool CanChangePlan => environment.IsDevelopment();

    public async Task<AccountPlanState> GetAsync(string userId)
    {
        var plan = await database.UserPlans.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => (AccountPlan?)item.Plan).SingleOrDefaultAsync() ?? AccountPlan.Standard;
        var statistics = configuration.GetValue<bool>($"Features:{plan}:LibraryStatistics");
        return new AccountPlanState(plan, statistics, CanChangePlan);
    }

    public async Task SetAsync(string userId, AccountPlan plan)
    {
        if (!CanChangePlan)
            throw new InvalidOperationException("Demo plan changes are available only in Development.");
        if (!Enum.IsDefined(plan))
            throw new ArgumentOutOfRangeException(nameof(plan));
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserPlans" ("UserId", "Plan") VALUES ({userId}, {(int)plan})
            ON CONFLICT ("UserId") DO UPDATE SET "Plan" = excluded."Plan"
            """);
    }
}
