using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtraJson",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Bible = table.Column<string>(type: "TEXT", nullable: false),
                    Outline = table.Column<string>(type: "TEXT", nullable: false),
                    TrackState = table.Column<bool>(type: "INTEGER", nullable: false),
                    Dice = table.Column<bool>(type: "INTEGER", nullable: false),
                    PassageWords = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stories_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stories_ConversationId",
                table: "Stories",
                column: "ConversationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropColumn(
                name: "ExtraJson",
                table: "Messages");
        }
    }
}
