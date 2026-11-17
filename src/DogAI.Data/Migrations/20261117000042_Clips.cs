using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class Clips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kept clips (M20). The three large values are the last columns on purpose, the video last of all: SQLite
            // keeps a large value in a chain of pages after the row and walks the whole chain to reach any column
            // behind it, so a list of rows (which reads the still) never reads a first frame or a video. A later
            // column must not be added behind them with AddColumn: rebuild the table, keeping Still, StartFrame and
            // Video last in that order (drop the three triggers below first and create them again afterwards, as the
            // PicturePreviews migration does for Pictures).
            migrationBuilder.CreateTable(
                name: "Clips",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    NegativePrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Frames = table.Column<int>(type: "INTEGER", nullable: false),
                    Fps = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Steps = table.Column<int>(type: "INTEGER", nullable: false),
                    Sampler = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Guidance = table.Column<double>(type: "REAL", nullable: false),
                    FlowShift = table.Column<double>(type: "REAL", nullable: false),
                    Decoder = table.Column<int>(type: "INTEGER", nullable: false),
                    Compression = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Vae = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    TextEncoder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    FastDecoder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    VideoBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartFrameBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Still = table.Column<byte[]>(type: "BLOB", nullable: true),
                    StartFrame = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Video = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clips", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clips_OwnerKind_OwnerId",
                table: "Clips",
                columns: new[] { "OwnerKind", "OwnerId" });

            // A clip's owner is a kind plus an id, so no foreign key can remove clips with their owner. These do, as
            // the Pictures triggers do for pictures: deleting a message (also when its whole chat or story is
            // deleted), a character or a persona takes its clips along, whichever code did the deleting. Clips kept
            // in the Video section (kind 'video') belong to nothing that can be deleted.
            migrationBuilder.Sql(
                "CREATE TRIGGER Clips_message_ad AFTER DELETE ON Messages BEGIN " +
                "DELETE FROM Clips WHERE OwnerKind = 'message' AND OwnerId = old.Id; END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER Clips_character_ad AFTER DELETE ON Characters BEGIN " +
                "DELETE FROM Clips WHERE OwnerKind = 'character' AND OwnerId = old.Id; END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER Clips_persona_ad AFTER DELETE ON Personas BEGIN " +
                "DELETE FROM Clips WHERE OwnerKind = 'persona' AND OwnerId = old.Id; END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_message_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_character_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_persona_ad;");
            migrationBuilder.DropTable(
                name: "Clips");
        }
    }
}
