using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class LyricsOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OffsetMs",
                table: "Lyrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OffsetMs",
                table: "Lyrics");
        }
    }
}
