using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hathor.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class TrackEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rec_Embeddings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VectorJson = table.Column<string>(type: "text", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rec_Embeddings", x => new { x.UserId, x.OwnerType, x.OwnerKey, x.Model });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rec_Embeddings_UserId_OwnerType_Model",
                table: "Rec_Embeddings",
                columns: new[] { "UserId", "OwnerType", "Model" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Rec_Embeddings");
        }
    }
}
