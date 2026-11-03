using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public class LibraryActivity
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    public int GameId { get; set; }
    [MaxLength(200)]
    public string GameName { get; set; } = "";
    [MaxLength(20)]
    public string Action { get; set; } = "";
    [MaxLength(20)]
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public record ActivityItem(long Id, int GameId, string GameName, string Action, string Status, DateTime CreatedUtc);
public record ActivityBatch(IReadOnlyList<ActivityItem> Events, long Cursor, bool HasMore);
