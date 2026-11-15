using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <summary>
    /// Kept pictures get a small preview in their own row (M16 step 1), and the table is rebuilt so that the image is
    /// the last column and the preview the one before it.
    /// <para>
    /// Why the rebuild: SQLite keeps a large value in a chain of pages after the row, and to read any column behind it
    /// it walks the whole chain. The Pictures table had the image fourth, so a list, a count or a search of prompts read
    /// every picture in full (measured: finding a word in 300 pictures of 0.6 MB read 180 MB). With the image last, the
    /// prompt, the sizes and the preview are read without touching it.
    /// </para>
    /// <para>
    /// A column added later with ALTER TABLE lands after the image, behind the chain again. Add it by rebuilding the
    /// table the same way, with the image kept last.
    /// </para>
    /// </summary>
    public partial class PicturePreviews : Migration
    {
        private const string Columns =
            "\"Id\", \"OwnerKind\", \"OwnerId\", \"ImageWidth\", \"ImageHeight\", \"ImageBytes\", \"Checkpoint\", \"Vae\", \"Prediction\", " +
            "\"Prompt\", \"NegativePrompt\", \"Width\", \"Height\", \"Seed\", \"Steps\", \"Sampler\", \"Scheduler\", \"Guidance\", \"ClipSkip\", " +
            "\"Loras\", \"Hires\", \"CreatedAt\"";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The triggers on Messages, Characters and Personas name the table; they are put back at the end.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_message_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_character_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Pictures_persona_ad;");

            migrationBuilder.Sql(
                "CREATE TABLE \"Pictures_new\" (" +
                "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Pictures\" PRIMARY KEY AUTOINCREMENT, " +
                "\"OwnerKind\" TEXT NOT NULL, " +
                "\"OwnerId\" INTEGER NOT NULL, " +
                "\"ImageWidth\" INTEGER NOT NULL, " +
                "\"ImageHeight\" INTEGER NOT NULL, " +
                "\"ImageBytes\" INTEGER NOT NULL, " +
                "\"Checkpoint\" TEXT NOT NULL, " +
                "\"Vae\" TEXT NULL, " +
                "\"Prediction\" INTEGER NOT NULL, " +
                "\"Prompt\" TEXT NOT NULL, " +
                "\"NegativePrompt\" TEXT NOT NULL, " +
                "\"Width\" INTEGER NOT NULL, " +
                "\"Height\" INTEGER NOT NULL, " +
                "\"Seed\" INTEGER NOT NULL, " +
                "\"Steps\" INTEGER NOT NULL, " +
                "\"Sampler\" TEXT NOT NULL, " +
                "\"Scheduler\" TEXT NULL, " +
                "\"Guidance\" REAL NOT NULL, " +
                "\"ClipSkip\" INTEGER NULL, " +
                "\"Loras\" TEXT NOT NULL, " +
                "\"Hires\" TEXT NULL, " +
                "\"CreatedAt\" TEXT NOT NULL, " +
                "\"Preview\" BLOB NULL, " +
                "\"Image\" BLOB NOT NULL);");
            migrationBuilder.Sql(
                $"INSERT INTO \"Pictures_new\" ({Columns}, \"Preview\", \"Image\") SELECT {Columns}, NULL, \"Image\" FROM \"Pictures\";");

            // The new table goes on counting ids where the old one stopped, so the id of a removed picture is never
            // given to another one (a passage remembers its picture by id).
            migrationBuilder.Sql("DELETE FROM sqlite_sequence WHERE name = 'Pictures_new';");
            migrationBuilder.Sql("INSERT INTO sqlite_sequence (name, seq) SELECT 'Pictures_new', seq FROM sqlite_sequence WHERE name = 'Pictures';");

            migrationBuilder.Sql("DROP TABLE \"Pictures\";");
            migrationBuilder.Sql("ALTER TABLE \"Pictures_new\" RENAME TO \"Pictures\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_Pictures_OwnerKind_OwnerId\" ON \"Pictures\" (\"OwnerKind\", \"OwnerId\");");

            // The same three rules as the Pictures migration: a deleted message, character or persona takes its pictures along.
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
            // The image stays last; only the preview goes.
            migrationBuilder.DropColumn(
                name: "Preview",
                table: "Pictures");
        }
    }
}
