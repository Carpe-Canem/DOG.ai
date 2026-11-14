using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Pictures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Image = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    Checkpoint = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Vae = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Prediction = table.Column<int>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    NegativePrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Steps = table.Column<int>(type: "INTEGER", nullable: false),
                    Sampler = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Scheduler = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Guidance = table.Column<double>(type: "REAL", nullable: false),
                    ClipSkip = table.Column<int>(type: "INTEGER", nullable: true),
                    Loras = table.Column<string>(type: "TEXT", nullable: false),
                    Hires = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pictures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pictures_OwnerKind_OwnerId",
                table: "Pictures",
                columns: new[] { "OwnerKind", "OwnerId" });

            // A picture's owner is a kind plus an id, so no foreign key can remove pictures with their owner. These do:
            // deleting a message (also when its whole chat or story is deleted), a character or a persona takes its
            // pictures along, whichever code did the deleting.
            migrationBuilder.Sql(
                "CREATE TRIGGER Pictures_message_ad AFTER DELETE ON Messages BEGIN " +
                "DELETE FROM Pictures WHERE OwnerKind = 'message' AND OwnerId = old.Id; END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER Pictures_character_ad AFTER DELETE ON Characters BEGIN " +
                "DELETE FROM Pictures WHERE OwnerKind = 'character' AND OwnerId = old.Id; END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER Pictures_persona_ad AFTER DELETE ON Personas BEGIN " +
                "DELETE FROM Pictures WHERE OwnerKind = 'persona' AND OwnerId = old.Id; END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_message_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_character_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_persona_ad;");
            migrationBuilder.DropTable(
                name: "Pictures");
        }
    }
}
