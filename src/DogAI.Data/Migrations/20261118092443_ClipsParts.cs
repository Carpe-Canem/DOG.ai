using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <summary>
    /// A kept clip records how many clips its video holds (M22 step 1): 1 for a clip as it was made, 2 or 3 for a clip
    /// joined with the clips it continues. Lists say a clip's length from it without reading any video, and the check
    /// on a kept clip stays exact (frames = parts × (frames − 1) + 1). Every clip kept before holds one clip.
    /// <para>
    /// The table is rebuilt, not altered, exactly as ClipsFaster did it and for the same reason: a column added with
    /// ALTER TABLE lands behind the video, and SQLite walks a large value's whole chain of pages to reach any column
    /// behind it. The three large values stay last, in their order: Still, StartFrame, Video. The next column on
    /// Clips is added the same way.
    /// </para>
    /// </summary>
    public partial class ClipsParts : Migration
    {
        /// <summary>Every column of the table as it was, up to and including Faster.</summary>
        private const string Before =
            "\"Id\", \"OwnerKind\", \"OwnerId\", \"Prompt\", \"NegativePrompt\", \"Width\", \"Height\", \"Frames\", \"Fps\", \"Seed\", " +
            "\"Steps\", \"Sampler\", \"Guidance\", \"FlowShift\", \"Decoder\", \"Compression\", \"Faster\"";

        private const string After =
            "\"Model\", \"Vae\", \"TextEncoder\", \"FastDecoder\", \"VideoBytes\", \"StartFrameBytes\", \"CreatedAt\", " +
            "\"Still\", \"StartFrame\", \"Video\"";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The triggers on Messages, Characters and Personas name the table; they are put back at the end.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_message_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_character_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Clips_persona_ad;");

            migrationBuilder.Sql(
                "CREATE TABLE \"Clips_new\" (" +
                "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Clips\" PRIMARY KEY AUTOINCREMENT, " +
                "\"OwnerKind\" TEXT NOT NULL, " +
                "\"OwnerId\" INTEGER NOT NULL, " +
                "\"Prompt\" TEXT NOT NULL, " +
                "\"NegativePrompt\" TEXT NOT NULL, " +
                "\"Width\" INTEGER NOT NULL, " +
                "\"Height\" INTEGER NOT NULL, " +
                "\"Frames\" INTEGER NOT NULL, " +
                "\"Fps\" INTEGER NOT NULL, " +
                "\"Seed\" INTEGER NOT NULL, " +
                "\"Steps\" INTEGER NOT NULL, " +
                "\"Sampler\" TEXT NOT NULL, " +
                "\"Guidance\" REAL NOT NULL, " +
                "\"FlowShift\" REAL NOT NULL, " +
                "\"Decoder\" INTEGER NOT NULL, " +
                "\"Compression\" INTEGER NOT NULL, " +
                "\"Faster\" INTEGER NOT NULL DEFAULT 0, " +
                "\"Parts\" INTEGER NOT NULL DEFAULT 1, " +
                "\"Model\" TEXT NOT NULL, " +
                "\"Vae\" TEXT NOT NULL, " +
                "\"TextEncoder\" TEXT NOT NULL, " +
                "\"FastDecoder\" TEXT NOT NULL, " +
                "\"VideoBytes\" INTEGER NOT NULL, " +
                "\"StartFrameBytes\" INTEGER NOT NULL, " +
                "\"CreatedAt\" TEXT NOT NULL, " +
                "\"Still\" BLOB NULL, " +
                "\"StartFrame\" BLOB NULL, " +
                "\"Video\" BLOB NOT NULL);");
            // Every clip there is holds one clip: nothing could join them.
            migrationBuilder.Sql(
                $"INSERT INTO \"Clips_new\" ({Before}, \"Parts\", {After}) SELECT {Before}, 1, {After} FROM \"Clips\";");

            // The new table goes on counting ids where the old one stopped, so the id of a removed clip is never
            // given to another one.
            migrationBuilder.Sql("DELETE FROM sqlite_sequence WHERE name = 'Clips_new';");
            migrationBuilder.Sql("INSERT INTO sqlite_sequence (name, seq) SELECT 'Clips_new', seq FROM sqlite_sequence WHERE name = 'Clips';");

            migrationBuilder.Sql("DROP TABLE \"Clips\";");
            migrationBuilder.Sql("ALTER TABLE \"Clips_new\" RENAME TO \"Clips\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_Clips_OwnerKind_OwnerId\" ON \"Clips\" (\"OwnerKind\", \"OwnerId\");");

            // The same three rules as the Clips migration: a deleted message, character or persona takes its clips along.
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
            // Only the one column goes, dropped by SQLite itself, in place, which leaves the table as M21 made it
            // (the large values still last). EF's DropColumn would rebuild the table, and renaming the rebuilt table
            // fails while the three triggers on other tables name it. No clip is lost: a joined clip stays in its
            // row, and the build that goes with this schema plays it as the one file it is, though it takes it for a
            // single clip of five seconds (its request is its last part's, as ever).
            migrationBuilder.Sql("ALTER TABLE \"Clips\" DROP COLUMN \"Parts\";");
        }
    }
}
