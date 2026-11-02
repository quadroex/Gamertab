using System.ComponentModel.DataAnnotations;

namespace Gamertab.Models;

public class RegisterInput
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
    [Required, MinLength(10), DataType(DataType.Password)]
    public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = "";
}

public class LoginInput
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class LibraryInput : IValidatableObject
{
    [Range(1, int.MaxValue)]
    public int GameId { get; set; }
    [EnumDataType(typeof(PlayStatus))]
    public PlayStatus Status { get; set; }
    [Range(0, 10)]
    public double? PersonalRating { get; set; }
    [StringLength(2000)]
    public string? Notes { get; set; }
    [DataType(DataType.Date)]
    public DateOnly? StartedOn { get; set; }
    [DataType(DataType.Date)]
    public DateOnly? CompletedOn { get; set; }
    public bool FollowNews { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (StartedOn.HasValue && CompletedOn.HasValue && CompletedOn < StartedOn)
            yield return new ValidationResult("Completion date must be on or after the start date.", [nameof(CompletedOn)]);
        if (CompletedOn.HasValue && Status != PlayStatus.Completed)
            yield return new ValidationResult("A completion date requires Completed status.", [nameof(CompletedOn)]);
    }

    public void ApplyTo(LibraryEntry entry)
    {
        entry.Status = Status;
        entry.PersonalRating = PersonalRating;
        entry.Notes = (Notes ?? "").Trim();
        entry.StartedOn = StartedOn;
        entry.CompletedOn = CompletedOn;
        entry.FollowNews = FollowNews;
    }
}

public class CollectionInput : IValidatableObject
{
    [Required, StringLength(100)]
    public string Name { get; set; } = "";
    [StringLength(1000)]
    public string? Description { get; set; }
    [StringLength(1000)]
    public string? CoverUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!string.IsNullOrWhiteSpace(CoverUrl) &&
            (!Uri.TryCreate(CoverUrl, UriKind.Absolute, out var url) || url.Scheme != "https"))
            yield return new ValidationResult("Use an HTTPS image URL.", [nameof(CoverUrl)]);
    }

    public void ApplyTo(GameCollection collection)
    {
        collection.Name = Name.Trim();
        collection.Description = (Description ?? "").Trim();
        collection.CoverUrl = (CoverUrl ?? "").Trim();
    }
}

public class CollectionGameInput
{
    [Range(1, int.MaxValue)]
    public int GameId { get; set; }
}

public record CatalogPage(List<Game> Games, string? Query, string? Genre, int Page, bool HasNextPage, bool IsLive, string? Notice = null)
{
    public List<Recommendation> Recommendations { get; init; } = [];
    public bool SavedOnly { get; init; }
    public static IReadOnlyDictionary<string, int> Genres { get; } = new Dictionary<string, int>
    {
        ["Adventure"] = 31,
        ["Indie"] = 32,
        ["Platform"] = 8,
        ["Puzzle"] = 9,
        ["Racing"] = 10,
        ["Role-playing"] = 12,
        ["Shooter"] = 5,
        ["Simulator"] = 13,
        ["Strategy"] = 15
    };
}
public record GamePage(Game Game, LibraryEntry? Entry, List<NewsItem> News, bool IgdbConfigured, SteamStoreInfo? Store = null, string? StoreNotice = null);
public record SteamStoreInfo(bool IsFree, decimal? Price, decimal? OriginalPrice, int DiscountPercent, DateTimeOffset CheckedAt, int? MetacriticScore = null, string? MetacriticUrl = null);
public record LibraryPage(List<LibraryEntry> Entries, string? Status);
public record LibraryEditPage(Game Game, LibraryInput Input);
public record Recommendation(Game Game, int Score, string Reason);
public record AccountSettingsPage(string Email, bool HasPassword, bool GoogleLinked, bool GoogleAvailable);
