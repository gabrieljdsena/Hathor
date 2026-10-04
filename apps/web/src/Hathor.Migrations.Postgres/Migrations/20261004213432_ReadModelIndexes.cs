using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReadModelIndexes : Migration
    {
        // Expression indexes matching the Dapper UPPER("UserId"::text)
        // predicate, composited with each query's ORDER BY / join columns.
        // Plain (UserId, …) indexes serve the EF Guid-equality paths.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Songs_User_Download\" " +
                "ON \"Songs\" (UPPER(\"UserId\"::text), \"DateDownloadUtc\" DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Podcasts_User_Download\" " +
                "ON \"Podcasts\" (UPPER(\"UserId\"::text), \"DateDownloadUtc\" DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Music_History_User_Played\" " +
                "ON \"Music_History\" (UPPER(\"UserId\"::text), \"DatePlayedUtc\" DESC, \"Id\" DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Playlist_History_User_Played\" " +
                "ON \"Playlist_History\" (UPPER(\"UserId\"::text), \"DatePlayedUtc\" DESC, \"Id\" DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Song_Playlist_User_Playlist\" " +
                "ON \"Song_Playlist\" (UPPER(\"UserId\"::text), \"PlaylistId\")");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Song_Playlist_User_File\" " +
                "ON \"Song_Playlist\" (UPPER(\"UserId\"::text), \"SongFile\")");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Playlists_User_Title\" " +
                "ON \"Playlists\" (UPPER(\"UserId\"::text), \"Title\")");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Podcast_Tags_User_Name\" " +
                "ON \"Podcast_Tags\" (UPPER(\"UserId\"::text), \"Name\")");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Podcast_Tag_Links_User\" " +
                "ON \"Podcast_Tag_Links\" (UPPER(\"UserId\"::text))");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Lyrics_User_File\" " +
                "ON \"Lyrics\" (\"UserId\", \"SongFile\")");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Download_Queue_User_Status\" " +
                "ON \"Download_Queue\" (\"UserId\", \"Status\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Songs_User_Download\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Podcasts_User_Download\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Music_History_User_Played\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Playlist_History_User_Played\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Song_Playlist_User_Playlist\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Song_Playlist_User_File\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Playlists_User_Title\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Podcast_Tags_User_Name\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Podcast_Tag_Links_User\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Lyrics_User_File\"");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Download_Queue_User_Status\"");
        }
    }
}
