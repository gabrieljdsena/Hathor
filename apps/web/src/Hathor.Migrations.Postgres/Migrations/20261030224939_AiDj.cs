using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AiDj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiDjSettingsJson",
                table: "User_Settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiDj_Clips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Mime = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DurationSec = table.Column<double>(type: "double precision", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDj_Clips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiDj_Feedback",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrackFile = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Event = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDj_Feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiDj_Sessions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDj_Sessions", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiDj_Clips_UserId_CreatedAtUtc",
                table: "AiDj_Clips",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiDj_Feedback_UserId_CreatedAtUtc",
                table: "AiDj_Feedback",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiDj_Feedback_UserId_SessionId",
                table: "AiDj_Feedback",
                columns: new[] { "UserId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiDj_Sessions_SessionId",
                table: "AiDj_Sessions",
                column: "SessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiDj_Clips");

            migrationBuilder.DropTable(
                name: "AiDj_Feedback");

            migrationBuilder.DropTable(
                name: "AiDj_Sessions");

            migrationBuilder.DropColumn(
                name: "AiDjSettingsJson",
                table: "User_Settings");
        }
    }
}
