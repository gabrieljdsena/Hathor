using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playback_Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SongFile = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PositionSec = table.Column<double>(type: "double precision", nullable: false),
                    DurationSec = table.Column<double>(type: "double precision", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    Skipped = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playback_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Playback_Events_UserId_SegmentId",
                table: "Playback_Events",
                columns: new[] { "UserId", "SegmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Playback_Events_UserId_SongFile",
                table: "Playback_Events",
                columns: new[] { "UserId", "SongFile" });

            migrationBuilder.CreateIndex(
                name: "IX_Playback_Events_UserId_StartedAtUtc",
                table: "Playback_Events",
                columns: new[] { "UserId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Playback_Events");
        }
    }
}
