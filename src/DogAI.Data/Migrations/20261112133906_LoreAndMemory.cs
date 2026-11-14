using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoreAndMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoreUsed",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryUsed",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorsNote",
                table: "Conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AuthorsNoteDepth",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4); // existing chats get the same depth as new ones

            migrationBuilder.AddColumn<bool>(
                name: "MemoryEnabled",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // memory is on for existing chats, as for new ones

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Conversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SummaryThroughId",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoreUsed",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MemoryUsed",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AuthorsNote",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AuthorsNoteDepth",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "MemoryEnabled",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SummaryThroughId",
                table: "Conversations");
        }
    }
}
