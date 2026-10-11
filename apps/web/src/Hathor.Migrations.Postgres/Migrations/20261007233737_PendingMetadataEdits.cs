using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PendingMetadataEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Pending_Metadata_Edits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    File = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsPodcast = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Artist = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Album = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Year = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Genre = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CoverArt = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pending_Metadata_Edits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pending_Metadata_Edits_UserId_File",
                table: "Pending_Metadata_Edits",
                columns: new[] { "UserId", "File" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pending_Metadata_Edits");
        }
    }
}
