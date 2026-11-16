using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AttachedPictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MeasuredVramProjector",
                table: "ModelRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Pictures on chat messages (M17). The image is the last column on purpose: SQLite walks a large value's
            // whole page chain to reach any column behind it, so with the image last a list of rows never reads one.
            // A later column must not be added behind it with AddColumn: rebuild the table, keeping Image last.
            // The rows go with their message (the foreign key cascades), whichever code deleted it.
            migrationBuilder.CreateTable(
                name: "AttachedPictures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Image = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachedPictures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttachedPictures_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachedPictures_ConversationId",
                table: "AttachedPictures",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachedPictures_MessageId_Position",
                table: "AttachedPictures",
                columns: new[] { "MessageId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachedPictures");

            // SQLite's own DROP COLUMN: EF's would rebuild the whole table for one column.
            migrationBuilder.Sql("ALTER TABLE ModelRecords DROP COLUMN MeasuredVramProjector;");
        }
    }
}
