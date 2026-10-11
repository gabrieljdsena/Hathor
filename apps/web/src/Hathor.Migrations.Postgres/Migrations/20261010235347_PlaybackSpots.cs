using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackSpots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playback_Spots",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    File = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PositionSec = table.Column<double>(type: "double precision", nullable: false),
                    IsPodcast = table.Column<bool>(type: "boolean", nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playback_Spots", x => new { x.UserId, x.DeviceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Playback_Spots_UserId_UpdatedUtc",
                table: "Playback_Spots",
                columns: new[] { "UserId", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Playback_Spots");
        }
    }
}
