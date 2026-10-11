using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SyncDeltaUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Song_Playlist",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Podcasts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Podcast_Tags",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Podcast_Tag_Links",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Playlists",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Lyrics",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Daily_Mix",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_Sync_Deletions_UserId_DeletedAtUtc",
                table: "Sync_Deletions",
                columns: new[] { "UserId", "DeletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_UserId_UpdatedAtUtc",
                table: "Songs",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Song_Playlist_UserId_UpdatedAtUtc",
                table: "Song_Playlist",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Podcasts_UserId_UpdatedAtUtc",
                table: "Podcasts",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Podcast_Tags_UserId_UpdatedAtUtc",
                table: "Podcast_Tags",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Podcast_Tag_Links_UserId_UpdatedAtUtc",
                table: "Podcast_Tag_Links",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_UserId_UpdatedAtUtc",
                table: "Playlists",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Lyrics_UserId_UpdatedAtUtc",
                table: "Lyrics",
                columns: new[] { "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Daily_Mix_UserId_UpdatedAtUtc",
                table: "Daily_Mix",
                columns: new[] { "UserId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sync_Deletions_UserId_DeletedAtUtc",
                table: "Sync_Deletions");

            migrationBuilder.DropIndex(
                name: "IX_Songs_UserId_UpdatedAtUtc",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Song_Playlist_UserId_UpdatedAtUtc",
                table: "Song_Playlist");

            migrationBuilder.DropIndex(
                name: "IX_Podcasts_UserId_UpdatedAtUtc",
                table: "Podcasts");

            migrationBuilder.DropIndex(
                name: "IX_Podcast_Tags_UserId_UpdatedAtUtc",
                table: "Podcast_Tags");

            migrationBuilder.DropIndex(
                name: "IX_Podcast_Tag_Links_UserId_UpdatedAtUtc",
                table: "Podcast_Tag_Links");

            migrationBuilder.DropIndex(
                name: "IX_Playlists_UserId_UpdatedAtUtc",
                table: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_Lyrics_UserId_UpdatedAtUtc",
                table: "Lyrics");

            migrationBuilder.DropIndex(
                name: "IX_Daily_Mix_UserId_UpdatedAtUtc",
                table: "Daily_Mix");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Song_Playlist");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Podcasts");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Podcast_Tags");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Podcast_Tag_Links");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Lyrics");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Daily_Mix");
        }
    }
}
