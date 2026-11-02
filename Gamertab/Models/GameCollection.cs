using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gamertab.Models;

public class GameCollection
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public IdentityUser User { get; set; } = null!;
    [MaxLength(100)]
    public string Name { get; set; } = "";
    [MaxLength(1000)]
    public string Description { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    [MaxLength(200)]
    public string? CoverBlobName { get; set; }
    public List<CollectionItem> Items { get; set; } = [];
}

public class CollectionItem
{
    public int CollectionId { get; set; }
    public GameCollection Collection { get; set; } = null!;
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
}
