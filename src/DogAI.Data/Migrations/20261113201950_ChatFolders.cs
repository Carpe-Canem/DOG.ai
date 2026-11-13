using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FolderId",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatFolders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Collapsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_FolderId",
                table: "Conversations",
                column: "FolderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatFolders");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_FolderId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Conversations");
        }
    }
}
