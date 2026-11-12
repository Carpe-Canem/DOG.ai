using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class Polish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Full-text search over message text. The index holds no copy of the text (it reads Messages through
            // content=), and three triggers keep it in step, including when a chat is deleted and takes its messages along.
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE MessageSearch USING fts5(Content, content='Messages', content_rowid='Id', tokenize='unicode61 remove_diacritics 2');");
            migrationBuilder.Sql(
                "CREATE TRIGGER MessageSearch_ai AFTER INSERT ON Messages BEGIN " +
                "INSERT INTO MessageSearch(rowid, Content) VALUES (new.Id, new.Content); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER MessageSearch_ad AFTER DELETE ON Messages BEGIN " +
                "INSERT INTO MessageSearch(MessageSearch, rowid, Content) VALUES ('delete', old.Id, old.Content); END;");
            migrationBuilder.Sql(
                "CREATE TRIGGER MessageSearch_au AFTER UPDATE OF Content ON Messages BEGIN " +
                "INSERT INTO MessageSearch(MessageSearch, rowid, Content) VALUES ('delete', old.Id, old.Content); " +
                "INSERT INTO MessageSearch(rowid, Content) VALUES (new.Id, new.Content); END;");
            migrationBuilder.Sql("INSERT INTO MessageSearch(MessageSearch) VALUES ('rebuild');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageSearch_ai;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageSearch_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageSearch_au;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS MessageSearch;");
            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "Conversations");
        }
    }
}
