using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Gamertab.Models;

public class Game
{
    public int Id { get; set; }
    public int? IgdbId { get; set; }
    public int? SteamAppId { get; set; }
    [MaxLength(200)]
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Genres { get; set; } = "";
    public string Themes { get; set; } = "";
    public string Modes { get; set; } = "";
    public string Platforms { get; set; } = "";
    [NotMapped]
    public string Perspectives { get; set; } = "";
    public DateOnly? ReleasedOn { get; set; }
    public double? UserRating { get; set; }
    public double? CriticRating { get; set; }
    public string AgeRatings { get; set; } = "";
    public double? MainHours { get; set; }
    public double? ExtrasHours { get; set; }
    public double? CompleteHours { get; set; }
    public int? TimeSubmissions { get; set; }
    public string SimilarIgdbIds { get; set; } = "";
    public string DataSource { get; set; } = "Starter catalogue";
}
