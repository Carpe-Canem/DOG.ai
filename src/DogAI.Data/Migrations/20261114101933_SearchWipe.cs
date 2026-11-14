using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchWipe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The search index keeps the words of a deleted message until it next tidies itself, which in a plain database
            // left them readable in the file. Its own "secure delete" takes them out at the moment of deleting.
            migrationBuilder.Sql("INSERT INTO MessageSearch(MessageSearch, rank) VALUES('secure-delete', 1);");
            // Built again from the messages that exist, so words of chats deleted before this change are dropped too.
            migrationBuilder.Sql("INSERT INTO MessageSearch(MessageSearch) VALUES('rebuild');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("INSERT INTO MessageSearch(MessageSearch, rank) VALUES('secure-delete', 0);");
        }
    }
}
