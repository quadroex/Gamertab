using Gamertab.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<IdentityUser>(options), IDataProtectionKeyContext
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();
    public DbSet<GameCollection> Collections => Set<GameCollection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<UserPlan> UserPlans => Set<UserPlan>();
    public DbSet<PlayJournal> PlayJournals => Set<PlayJournal>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PlayJournal>().HasKey(journal => new { journal.UserId, journal.GameId });
        builder.Entity<UserPlan>().HasOne(plan => plan.User).WithOne().HasForeignKey<UserPlan>(plan => plan.UserId);
        builder.Entity<Game>().HasIndex(game => game.IgdbId).IsUnique();
        builder.Entity<LibraryEntry>().HasIndex(entry => new { entry.UserId, entry.GameId }).IsUnique();
        builder.Entity<CollectionItem>().HasKey(item => new { item.CollectionId, item.GameId });
        builder.Entity<NewsItem>().HasIndex(item => new { item.GameId, item.SourceId }).IsUnique();
        builder.Entity<NewsItem>().Property(item => item.PublishedAt).HasConversion(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
    }
}
