using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Trigram + fuzzy-string matching for DB-level typo-tolerant
            // search (backs the in-memory FuzzySearch read-models).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS fuzzystrmatch");

            migrationBuilder.CreateTable(
                name: "Api_Keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    Scopes = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Api_Keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Daily_Mix",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MixDate = table.Column<string>(type: "text", nullable: false),
                    SongFilesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Daily_Mix", x => new { x.UserId, x.MixDate });
                });

            migrationBuilder.CreateTable(
                name: "Download_Queue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Qid = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Artist = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Filename = table.Column<string>(type: "text", nullable: true),
                    IsPodcast = table.Column<bool>(type: "boolean", nullable: false),
                    TargetFile = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Download_Queue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lyrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongFile = table.Column<string>(type: "text", nullable: false),
                    LyricsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lyrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Music_History",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongFile = table.Column<string>(type: "text", nullable: false),
                    DatePlayedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Music_History", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Playback_State",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentFile = table.Column<string>(type: "text", nullable: true),
                    CurrentIsPodcast = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentPlaylistId = table.Column<long>(type: "bigint", nullable: true),
                    IsPlaying = table.Column<bool>(type: "boolean", nullable: false),
                    FirstPlay = table.Column<bool>(type: "boolean", nullable: false),
                    Shuffle = table.Column<bool>(type: "boolean", nullable: false),
                    Repeat = table.Column<bool>(type: "boolean", nullable: false),
                    Volume = table.Column<double>(type: "double precision", nullable: false),
                    PreviousVolume = table.Column<double>(type: "double precision", nullable: false),
                    NextFilesJson = table.Column<string>(type: "text", nullable: false),
                    PrevFilesJson = table.Column<string>(type: "text", nullable: false),
                    UnshuffledFilesJson = table.Column<string>(type: "text", nullable: false),
                    QueueSourceJson = table.Column<string>(type: "text", nullable: true),
                    IsCustomQueue = table.Column<bool>(type: "boolean", nullable: false),
                    FallbackToGeneralList = table.Column<bool>(type: "boolean", nullable: false),
                    PositionOffsetSec = table.Column<double>(type: "double precision", nullable: false),
                    PausePositionSec = table.Column<double>(type: "double precision", nullable: false),
                    LastPlayUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playback_State", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Playlist_History",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaylistId = table.Column<long>(type: "bigint", nullable: false),
                    DatePlayedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlist_History", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Thumbnail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Podcast_Tag_Links",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PodcastFile = table.Column<string>(type: "text", nullable: false),
                    TagId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Podcast_Tag_Links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Podcast_Tags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Podcast_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Podcasts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    File = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DownloadedLink = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    DateDownloadUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Artist = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Podcasts", x => new { x.UserId, x.File });
                });

            migrationBuilder.CreateTable(
                name: "Refresh_Tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refresh_Tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "text", nullable: false),
                    RememberMe = table.Column<bool>(type: "boolean", nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Song_Playlist",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongFile = table.Column<string>(type: "text", nullable: false),
                    PlaylistId = table.Column<long>(type: "bigint", nullable: false),
                    DateAddedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Song_Playlist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Songs",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    File = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DownloadedLink = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DateDownloadUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Artist = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Songs", x => new { x.UserId, x.File });
                });

            migrationBuilder.CreateTable(
                name: "Sync_Deletions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableName = table.Column<string>(type: "text", nullable: false),
                    RowKey = table.Column<string>(type: "text", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sync_Deletions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User_Settings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Volume = table.Column<double>(type: "double precision", nullable: false),
                    LimitDownloads = table.Column<int>(type: "integer", nullable: false),
                    BackgroundPath = table.Column<string>(type: "text", nullable: true),
                    CrossfadeEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CrossfadeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    LastRoute = table.Column<string>(type: "text", nullable: true),
                    Browser = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User_Settings", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Api_Keys_TokenHash",
                table: "Api_Keys",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Download_Queue_Qid",
                table: "Download_Queue",
                column: "Qid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Podcast_Tag_Links_UserId_PodcastFile_TagId",
                table: "Podcast_Tag_Links",
                columns: new[] { "UserId", "PodcastFile", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Podcast_Tags_UserId_Name",
                table: "Podcast_Tags",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refresh_Tokens_TokenHash",
                table: "Refresh_Tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_RefreshTokenHash",
                table: "Sessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId_RevokedAtUtc",
                table: "Sessions",
                columns: new[] { "UserId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Api_Keys");

            migrationBuilder.DropTable(
                name: "Daily_Mix");

            migrationBuilder.DropTable(
                name: "Download_Queue");

            migrationBuilder.DropTable(
                name: "Lyrics");

            migrationBuilder.DropTable(
                name: "Music_History");

            migrationBuilder.DropTable(
                name: "Playback_State");

            migrationBuilder.DropTable(
                name: "Playlist_History");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropTable(
                name: "Podcast_Tag_Links");

            migrationBuilder.DropTable(
                name: "Podcast_Tags");

            migrationBuilder.DropTable(
                name: "Podcasts");

            migrationBuilder.DropTable(
                name: "Refresh_Tokens");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Song_Playlist");

            migrationBuilder.DropTable(
                name: "Songs");

            migrationBuilder.DropTable(
                name: "Sync_Deletions");

            migrationBuilder.DropTable(
                name: "User_Settings");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
