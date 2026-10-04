using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class ReadModelIndexes : Migration
    {
        // Functional key parts matching the Dapper UPPER(UserId) predicate
        // (MySQL 8 / TiDB 5+ expression indexes), composited with each
        // query's ORDER BY / join columns. Plain (UserId, …) indexes serve
        // the EF equality paths.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Songs_User_Download` ON `Songs` ((UPPER(`UserId`)), `DateDownloadUtc` DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Podcasts_User_Download` ON `Podcasts` ((UPPER(`UserId`)), `DateDownloadUtc` DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Music_History_User_Played` ON `Music_History` ((UPPER(`UserId`)), `DatePlayedUtc` DESC, `Id` DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Playlist_History_User_Played` ON `Playlist_History` ((UPPER(`UserId`)), `DatePlayedUtc` DESC, `Id` DESC)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Song_Playlist_User_Playlist` ON `Song_Playlist` ((UPPER(`UserId`)), `PlaylistId`)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Song_Playlist_User_File` ON `Song_Playlist` ((UPPER(`UserId`)), `SongFile`)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Playlists_User_Title` ON `Playlists` ((UPPER(`UserId`)), `Title`)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Podcast_Tags_User_Name` ON `Podcast_Tags` ((UPPER(`UserId`)), `Name`)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Podcast_Tag_Links_User` ON `Podcast_Tag_Links` ((UPPER(`UserId`)))");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Lyrics_User_File` ON `Lyrics` (`UserId`, `SongFile`)");
            migrationBuilder.Sql(
                "CREATE INDEX `IX_Download_Queue_User_Status` ON `Download_Queue` (`UserId`, `Status`)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX `IX_Songs_User_Download` ON `Songs`");
            migrationBuilder.Sql("DROP INDEX `IX_Podcasts_User_Download` ON `Podcasts`");
            migrationBuilder.Sql("DROP INDEX `IX_Music_History_User_Played` ON `Music_History`");
            migrationBuilder.Sql("DROP INDEX `IX_Playlist_History_User_Played` ON `Playlist_History`");
            migrationBuilder.Sql("DROP INDEX `IX_Song_Playlist_User_Playlist` ON `Song_Playlist`");
            migrationBuilder.Sql("DROP INDEX `IX_Song_Playlist_User_File` ON `Song_Playlist`");
            migrationBuilder.Sql("DROP INDEX `IX_Playlists_User_Title` ON `Playlists`");
            migrationBuilder.Sql("DROP INDEX `IX_Podcast_Tags_User_Name` ON `Podcast_Tags`");
            migrationBuilder.Sql("DROP INDEX `IX_Podcast_Tag_Links_User` ON `Podcast_Tag_Links`");
            migrationBuilder.Sql("DROP INDEX `IX_Lyrics_User_File` ON `Lyrics`");
            migrationBuilder.Sql("DROP INDEX `IX_Download_Queue_User_Status` ON `Download_Queue`");
        }
    }
}
