using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<ApiToken> ApiTokens { get; set; }
    public DbSet<UserTotp> UserTotps { get; set; }
    public DbSet<TrustedDevice> TrustedDevices { get; set; }
    public DbSet<WebhookSubscription> WebhookSubscriptions { get; set; }
    public DbSet<Library> Libraries { get; set; }
    public DbSet<MediaItem> MediaItems { get; set; }
    public DbSet<AppSetting> Settings { get; set; }
    public DbSet<Invite> Invites { get; set; }
    public DbSet<UserMediaInteraction> UserMediaInteractions { get; set; }
    public DbSet<UserSeriesPreference> UserSeriesPreferences { get; set; }
    public DbSet<UserPreference> UserPreferences { get; set; }
    public DbSet<UserReaderPreferences> UserReaderPreferences { get; set; }
    public DbSet<Bookmark> Bookmarks { get; set; }
    public DbSet<Highlight> Highlights { get; set; }
    public DbSet<ReadingSession> ReadingSessions { get; set; }
    public DbSet<SystemNotification> SystemNotifications { get; set; }
    public DbSet<LibraryRecentCache> LibraryRecentCaches { get; set; }
    public DbSet<HeroCache> HeroCaches { get; set; }

    // Normalized relational tables (replacing JSON-trapped data)
    public DbSet<Person> Persons { get; set; }
    public DbSet<MediaItemCast> MediaItemCasts { get; set; }
    public DbSet<Genre> Genres { get; set; }
    public DbSet<MediaItemGenre> MediaItemGenres { get; set; }

    public DbSet<AudioTrack> AudioTracks { get; set; }
    public DbSet<SubtitleTrack> SubtitleTracks { get; set; }
    public DbSet<Chapter> Chapters { get; set; }
    public DbSet<MediaFingerprint> MediaFingerprints { get; set; }
    public DbSet<ProviderMetadataCache> ProviderMetadataCaches { get; set; }

    // Persistent retry queue
    public DbSet<MetadataRetry> MetadataRetries { get; set; }

    // Per-user library allow-list (Wave C). See UserLibraryAccess.cs for semantics.
    public DbSet<UserLibraryAccess> UserLibraryAccess { get; set; }

    // Wave E1 — user-owned audio playlists.
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistItem> PlaylistItems { get; set; }

    // Wave E2 — movie collections / franchises.
    public DbSet<Collection> Collections { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        // RefreshToken Configuration (Todo 04 — refresh-token persistence + rotation)
        // - TokenHash is unique so lookups by raw-token-hash are a direct index hit.
        // - (UserId, RevokedAt) supports "revoke all active tokens for user X" in one scan.
        // - ReplacedByTokenId is a nullable self-FK; SetNull on delete so pruning old rows
        //   doesn't orphan a reference on surviving rows.
        modelBuilder.Entity<RefreshToken>()
            .HasKey(rt => rt.Id);

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(rt => rt.TokenHash)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(rt => new { rt.UserId, rt.RevokedAt });

        modelBuilder.Entity<RefreshToken>()
            .HasOne(rt => rt.User)
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RefreshToken>()
            .HasOne(rt => rt.ReplacedByToken)
            .WithMany()
            .HasForeignKey(rt => rt.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.SetNull);

        // ApiToken Configuration (P1-WI-002 — long-lived programmatic credentials).
        // Mirrors RefreshToken: unique hash index for direct lookup, cascade on user delete.
        modelBuilder.Entity<ApiToken>().HasKey(at => at.Id);
        modelBuilder.Entity<ApiToken>().HasIndex(at => at.TokenHash).IsUnique();
        modelBuilder.Entity<ApiToken>().HasIndex(at => new { at.UserId, at.RevokedAt });
        modelBuilder.Entity<ApiToken>()
            .HasOne(at => at.User)
            .WithMany()
            .HasForeignKey(at => at.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserTotp Configuration (P2-WI-005 — one TOTP enrollment per user).
        modelBuilder.Entity<UserTotp>().HasKey(t => t.UserId);
        modelBuilder.Entity<UserTotp>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // TrustedDevice: remembered 2FA devices, cleared when the user is deleted.
        modelBuilder.Entity<TrustedDevice>()
            .HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // WebhookSubscription Configuration (P2-WI-004).
        modelBuilder.Entity<WebhookSubscription>().HasKey(w => w.Id);
        modelBuilder.Entity<WebhookSubscription>().HasIndex(w => w.UserId);
        modelBuilder.Entity<WebhookSubscription>()
            .HasOne(w => w.User)
            .WithMany()
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var stringListComparer = new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
            (c1, c2) => c1!.SequenceEqual(c2!),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList());

        modelBuilder.Entity<Library>()
            .Property(l => l.Paths)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()
            )
            .Metadata.SetValueComparer(stringListComparer);

        modelBuilder.Entity<Invite>()
            .HasIndex(i => i.Code)
            .IsUnique();

        // UserMediaInteraction Configuration
        modelBuilder.Entity<UserMediaInteraction>()
            .HasKey(umi => new { umi.UserId, umi.MediaItemId });

        modelBuilder.Entity<UserMediaInteraction>()
            .HasOne(umi => umi.User)
            .WithMany()
            .HasForeignKey(umi => umi.UserId);

        modelBuilder.Entity<UserMediaInteraction>()
            .HasOne(umi => umi.MediaItem)
            .WithMany()
            .HasForeignKey(umi => umi.MediaItemId);

        // MediaItem Self-Referencing Relationship (Series -> Episodes)
        modelBuilder.Entity<MediaItem>()
            .HasOne(m => m.Series)
            .WithMany()
            .HasForeignKey(m => m.SeriesId)
            .OnDelete(DeleteBehavior.Cascade);

        // MediaItem Self-Referencing Relationship (Season -> Episodes)
        // Use ClientSetNull to avoid multiple cascade paths (Series->Episode and Series->Season->Episode)
        modelBuilder.Entity<MediaItem>()
            .HasOne(m => m.Season)
            .WithMany()
            .HasForeignKey(m => m.SeasonId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        // MediaItem Self-Referencing Relationship (Artist -> Albums/Tracks)
        modelBuilder.Entity<MediaItem>()
            .HasOne(m => m.Artist)
            .WithMany()
            .HasForeignKey(m => m.ArtistId)
            .OnDelete(DeleteBehavior.Cascade);

        // MediaItem Self-Referencing Relationship (Album -> Tracks)
        modelBuilder.Entity<MediaItem>()
            .HasOne(m => m.Album)
            .WithMany()
            .HasForeignKey(m => m.AlbumId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserSeriesPreference Configuration (subtitle preferences per TV show)
        modelBuilder.Entity<UserSeriesPreference>()
            .HasKey(usp => new { usp.UserId, usp.SeriesId });

        modelBuilder.Entity<UserSeriesPreference>()
            .HasOne(usp => usp.User)
            .WithMany()
            .HasForeignKey(usp => usp.UserId);

        modelBuilder.Entity<UserSeriesPreference>()
            .HasOne(usp => usp.Series)
            .WithMany()
            .HasForeignKey(usp => usp.SeriesId);

        // UserReaderPreferences Configuration (ER-012)
        // Composite PK on (UserId, MediaItemId) — at most one override row per
        // user per book. Cascade on both sides: delete a user / delete a media
        // item, its reader overrides go with it.
        modelBuilder.Entity<UserReaderPreferences>()
            .HasKey(p => new { p.UserId, p.MediaItemId });

        modelBuilder.Entity<UserReaderPreferences>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserReaderPreferences>()
            .HasOne(p => p.MediaItem)
            .WithMany()
            .HasForeignKey(p => p.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Bookmark Configuration (ER-023)
        // Many bookmarks per (user, book). Index on (UserId, MediaItemId) so the
        // list endpoint scans a hot path. Cascade from both sides so deleting
        // a user or a media item carries their bookmarks along.
        modelBuilder.Entity<Bookmark>()
            .HasKey(b => b.Id);

        modelBuilder.Entity<Bookmark>()
            .HasIndex(b => new { b.UserId, b.MediaItemId });

        modelBuilder.Entity<Bookmark>()
            .HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Bookmark>()
            .HasOne(b => b.MediaItem)
            .WithMany()
            .HasForeignKey(b => b.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Highlight Configuration (ER-040)
        // Same layout as Bookmark — many highlights per (user, book); indexed
        // for the list hot path; cascade both directions.
        modelBuilder.Entity<Highlight>()
            .HasKey(h => h.Id);

        modelBuilder.Entity<Highlight>()
            .HasIndex(h => new { h.UserId, h.MediaItemId });

        modelBuilder.Entity<Highlight>()
            .HasOne(h => h.User)
            .WithMany()
            .HasForeignKey(h => h.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Highlight>()
            .HasOne(h => h.MediaItem)
            .WithMany()
            .HasForeignKey(h => h.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // ReadingSession Configuration (ER-052)
        // Indexed on (UserId, MediaItemId) for the per-book summary hot path.
        // Cascade on both sides — deleting a user / a book removes the
        // associated sessions.
        modelBuilder.Entity<ReadingSession>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<ReadingSession>()
            .HasIndex(s => new { s.UserId, s.MediaItemId });

        modelBuilder.Entity<ReadingSession>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ReadingSession>()
            .HasOne(s => s.MediaItem)
            .WithMany()
            .HasForeignKey(s => s.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserPreference Configuration (global user preferences)
        modelBuilder.Entity<UserPreference>()
            .HasOne(up => up.User)
            .WithMany()
            .HasForeignKey(up => up.UserId)
            .OnDelete(DeleteBehavior.Cascade); // Clean up when user is deleted

        // --- Normalized Cast/Genre Tables ---

        // MediaItemCast: uses surrogate PK `Id`
        modelBuilder.Entity<MediaItemCast>()
            .HasOne(mc => mc.MediaItem)
            .WithMany(m => m.MediaItemCasts)
            .HasForeignKey(mc => mc.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MediaItemCast>()
            .HasOne(mc => mc.Person)
            .WithMany()
            .HasForeignKey(mc => mc.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        // MediaItemGenre: composite PK on both FKs
        modelBuilder.Entity<MediaItemGenre>()
            .HasKey(mg => new { mg.MediaItemId, mg.GenreId });

        modelBuilder.Entity<MediaItemGenre>()
            .HasOne(mg => mg.MediaItem)
            .WithMany(m => m.MediaItemGenres)
            .HasForeignKey(mg => mg.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MediaItemGenre>()
            .HasOne(mg => mg.Genre)
            .WithMany()
            .HasForeignKey(mg => mg.GenreId)
            .OnDelete(DeleteBehavior.Cascade);

        // MediaItem Tracks and Chapters Configurations
        modelBuilder.Entity<AudioTrack>()
            .HasOne(at => at.MediaItem)
            .WithMany(m => m.AudioTracks)
            .HasForeignKey(at => at.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SubtitleTrack>()
            .HasOne(st => st.MediaItem)
            .WithMany(m => m.SubtitleTracks)
            .HasForeignKey(st => st.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Chapter>()
            .HasOne(ch => ch.MediaItem)
            .WithMany(m => m.Chapters)
            .HasForeignKey(ch => ch.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // MediaFingerprint: one row per media item, cascades with the parent.
        // Unique index on MediaItemId enforces the 1:1 relationship at the DB level.
        modelBuilder.Entity<MediaFingerprint>()
            .HasOne(f => f.MediaItem)
            .WithOne()
            .HasForeignKey<MediaFingerprint>(f => f.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserLibraryAccess (Wave C): per-user allow-list of libraries.
        // Both FKs cascade — deleting a Library or hard-deleting a User wipes
        // the allow-list rows automatically. Soft-delete on User (the only flow
        // currently exercised) does not remove the user row, so the rows persist.
        modelBuilder.Entity<UserLibraryAccess>()
            .HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserLibraryAccess>()
            .HasOne(a => a.Library)
            .WithMany()
            .HasForeignKey(a => a.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Wave E1 — Playlists. Owner cascade so deleting a user removes their
        // private playlists. PlaylistItems cascade with their parent Playlist.
        // MediaItem deletion sets PlaylistItem.MediaItemId-bound rows to cascade
        // (a removed track is removed from any playlist containing it; that's
        // standard for media servers — the playlist remains, the missing track
        // simply disappears from the ordered list).
        modelBuilder.Entity<Playlist>()
            .HasOne(p => p.Owner)
            .WithMany()
            .HasForeignKey(p => p.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlaylistItem>()
            .HasOne(pi => pi.Playlist)
            .WithMany(p => p.Items)
            .HasForeignKey(pi => pi.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlaylistItem>()
            .HasOne(pi => pi.MediaItem)
            .WithMany()
            .HasForeignKey(pi => pi.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Wave E2 — Collection ↔ MediaItem. SetNull on Collection delete so
        // the movie stays in the library; only the franchise grouping goes.
        modelBuilder.Entity<MediaItem>()
            .HasOne(m => m.Collection)
            .WithMany(c => c.Items)
            .HasForeignKey(m => m.CollectionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MediaItem>()
            .HasIndex(m => m.CollectionId);
    }
}

