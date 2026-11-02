using System.ComponentModel.DataAnnotations;

namespace Gamertab.Models;

public record ChatMessage(string Role, string Text);
public class AssistantInput : IValidatableObject
{
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = "";
    [MaxLength(10)]
    public List<ChatMessage> History { get; set; } = [];
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(Message) || History is null || History.Any(item => item is null || item.Role is not ("user" or "model") || item.Text is null || item.Text.Length > 8000))
            yield return new ValidationResult("Invalid conversation.");
    }
}
