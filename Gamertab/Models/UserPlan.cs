using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public enum AccountPlan { Standard, Premium }

public class UserPlan
{
    [Key]
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    public AccountPlan Plan { get; set; }
}

public record AccountPlanState(AccountPlan Plan, bool CanViewStatistics, bool CanChangePlan);
