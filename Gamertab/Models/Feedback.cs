using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public enum FeedbackCategory { Bug, Suggestion, GameInformation }

public class FeedbackInput
{
    [EnumDataType(typeof(FeedbackCategory))]
    public FeedbackCategory Category { get; set; }
    [Required, StringLength(150, MinimumLength = 3)]
    public string Subject { get; set; } = "";
    [Required, StringLength(4000, MinimumLength = 10)]
    public string Message { get; set; } = "";
    [EmailAddress, StringLength(254)]
    public string? ReplyEmail { get; set; }
    [Range(1, int.MaxValue)]
    public int? GameId { get; set; }
}

public class FeedbackRequest
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    [MaxLength(150)]
    public string Subject { get; set; } = "";
    public FeedbackCategory Category { get; set; }
    [MaxLength(40)]
    public string TicketId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public record FeedbackTicketPage(FeedbackRequest Request, string? Status, string? Message);
