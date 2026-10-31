using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public enum PlayStatus { Planned, Playing, Completed, OnHold, Dropped }

public class LibraryEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public PlayStatus Status { get; set; }
    public double? PersonalRating { get; set; }
    [MaxLength(2000)]
    public string Notes { get; set; } = "";
    public DateOnly? StartedOn { get; set; }
    public DateOnly? CompletedOn { get; set; }
    public bool FollowNews { get; set; } = true;
}
