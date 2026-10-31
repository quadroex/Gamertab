namespace Gamertab.Models;

public class NewsItem
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public string SourceId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
}
