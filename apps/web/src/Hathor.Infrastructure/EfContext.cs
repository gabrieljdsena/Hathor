using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hathor.Infrastructure.Ef;

// Table names mirror desktop database.sql (Songs, Podcasts, Playlists, ...)
// plus UserId on every user-scoped table (multi-user delta).
public sealed class HathorDbContext(DbContextOptions<HathorDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Song> Songs => Set<Song>();
    public DbSet<Podcast> Podcasts => Set<Podcast>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<SongPlaylist> SongPlaylists => Set<SongPlaylist>();
    public DbSet<PodcastTag> PodcastTags => Set<PodcastTag>();
    public DbSet<PodcastTagLink> PodcastTagLinks => Set<PodcastTagLink>();
    public DbSet<PodcastTimestamp> PodcastTimestamps => Set<PodcastTimestamp>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<Lyric> Lyrics => Set<Lyric>();
    public DbSet<PendingMetadataEdit> PendingMetadataEdits => Set<PendingMetadataEdit>();
    public DbSet<MusicHistoryEntry> MusicHistory => Set<MusicHistoryEntry>();
    public DbSet<PlaylistHistoryEntry> PlaylistHistory => Set<PlaylistHistoryEntry>();
    public DbSet<SyncDeletion> SyncDeletions => Set<SyncDeletion>();
    public DbSet<DailyMix> DailyMixes => Set<DailyMix>();
    public DbSet<DiscoverCache> DiscoverCaches => Set<DiscoverCache>();
    public DbSet<UserSettings> Settings => Set<UserSettings>();
    public DbSet<PlaybackStateRow> PlaybackStates => Set<PlaybackStateRow>();

    // Delta-sync change feed: every insert/update stamps UpdatedAtUtc, so
    // GET /sync/delta needs no per-write-path bookkeeping. Server clock only.
    public override int SaveChanges()
    {
        StampUpdatedAt();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampUpdatedAt();
        return base.SaveChangesAsync(ct);
    }

    private void StampUpdatedAt()
    {
        var now = DateTime.UtcNow;
        foreach (var e in ChangeTracker.Entries<ITrackUpdatedAt>())
            if (e.State is EntityState.Added or EntityState.Modified)
                e.Entity.UpdatedAtUtc = now;
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
        });
        b.Entity<Session>(e =>
        {
            e.ToTable("Sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.RevokedAtUtc });
            e.Property(x => x.DeviceLabel).HasMaxLength(128);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });
        b.Entity<ApiKey>(e =>
        {
            e.ToTable("Api_Keys");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });
        b.Entity<Song>(e =>
        {
            e.ToTable("Songs");
            e.HasKey(x => new { x.UserId, x.File });
            e.Property(x => x.File).HasMaxLength(255);
            e.Property(x => x.Title).HasMaxLength(255);
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<Podcast>(e =>
        {
            e.ToTable("Podcasts");
            e.HasKey(x => new { x.UserId, x.File });
            e.Property(x => x.File).HasMaxLength(255);
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<Playlist>(e =>
        {
            e.ToTable("Playlists");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<SongPlaylist>(e =>
        {
            e.ToTable("Song_Playlist");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<PodcastTag>(e =>
        {
            e.ToTable("Podcast_Tags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<PodcastTagLink>(e =>
        {
            e.ToTable("Podcast_Tag_Links");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.PodcastFile, x.TagId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<PodcastTimestamp>(e =>
        {
            e.ToTable("Podcast_Timestamps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PodcastFile).HasMaxLength(255);
            e.Property(x => x.Name).HasMaxLength(255);
            e.HasIndex(x => new { x.UserId, x.PodcastFile });
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<DownloadJob>(e =>
        {
            e.ToTable("Download_Queue");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Qid).IsUnique();
        });
        b.Entity<Lyric>(e =>
        {
            e.ToTable("Lyrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<PendingMetadataEdit>(e =>
        {
            e.ToTable("Pending_Metadata_Edits");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.File).HasMaxLength(255);
            e.Property(x => x.Title).HasMaxLength(255);
            e.Property(x => x.Artist).HasMaxLength(255);
            e.Property(x => x.Album).HasMaxLength(255);
            e.Property(x => x.Year).HasMaxLength(16);
            e.Property(x => x.Genre).HasMaxLength(64);
            // CoverArt holds data: URLs (often 100KB+) — unlimited text.
            e.HasIndex(x => new { x.UserId, x.File }).IsUnique();
        });
        b.Entity<MusicHistoryEntry>(e =>
        {
            e.ToTable("Music_History");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
        });
        b.Entity<PlaylistHistoryEntry>(e =>
        {
            e.ToTable("Playlist_History");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
        });
        b.Entity<SyncDeletion>(e =>
        {
            e.ToTable("Sync_Deletions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.DeletedAtUtc });
        });
        b.Entity<DailyMix>(e =>
        {
            e.ToTable("Daily_Mix");
            e.HasKey(x => new { x.UserId, x.MixDate });
            e.HasIndex(x => new { x.UserId, x.UpdatedAtUtc });
        });
        b.Entity<DiscoverCache>(e =>
        {
            e.ToTable("Discover_Cache");
            e.HasKey(x => new { x.UserId, x.Date });
        });
        b.Entity<UserSettings>(e =>
        {
            e.ToTable("User_Settings");
            e.HasKey(x => x.UserId);
        });
        b.Entity<PlaybackStateRow>(e =>
        {
            e.ToTable("Playback_State");
            e.HasKey(x => x.UserId);
        });
    }
}

// EF persistence shape for the PlaybackState aggregate (lists as JSON).
public sealed class PlaybackStateRow
{
    public Guid UserId { get; set; }
    public string? CurrentFile { get; set; }
    public bool CurrentIsPodcast { get; set; }
    public long? CurrentPlaylistId { get; set; }
    public bool IsPlaying { get; set; }
    public bool FirstPlay { get; set; } = true;
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }
    public double Volume { get; set; } = 0.7;
    public double PreviousVolume { get; set; } = 1.0;
    public string NextFilesJson { get; set; } = "[]";
    public string PrevFilesJson { get; set; } = "[]";
    public string UnshuffledFilesJson { get; set; } = "[]";
    public string? QueueSourceJson { get; set; }
    public bool IsCustomQueue { get; set; }
    public bool FallbackToGeneralList { get; set; } = true;
    public double PositionOffsetSec { get; set; }
    public double PausePositionSec { get; set; }
    public DateTime LastPlayUtc { get; set; }
    public static readonly ValueConverter<List<string>, string> ListJsonConverter =
        new(
            v => System.Text.Json.JsonSerializer.Serialize(v),
            v => string.IsNullOrWhiteSpace(v)
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>());
}
