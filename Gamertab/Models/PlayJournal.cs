using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public class PlayJournal
{
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int Revision { get; set; }
    public string BlocksJson { get; set; } = "[]";
}

public record JournalBlock(string Type, string Text);
public record JournalSnapshot(int Revision, IReadOnlyList<JournalBlock> Blocks, string OwnerId);
public class JournalInput : IValidatableObject
{
    [Range(0, int.MaxValue - 1)]
    public int Revision { get; set; }
    [Required, MaxLength(100)]
    public List<JournalBlock> Blocks { get; set; } = [];
    public Guid ClientId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Blocks is not null && Blocks.Any(block => block is null || block.Type is not ("paragraph" or "heading" or "list") || block.Text is null || block.Text.Length > 10000))
            yield return new ValidationResult("Invalid journal blocks.", [nameof(Blocks)]);
    }
}
