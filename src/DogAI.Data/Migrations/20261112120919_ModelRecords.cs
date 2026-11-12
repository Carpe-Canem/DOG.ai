using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Repo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Quant = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UseCase = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    BenchTokensPerSecond = table.Column<double>(type: "REAL", nullable: true),
                    BenchReadPerSecond = table.Column<double>(type: "REAL", nullable: true),
                    TunedGpuLayers = table.Column<int>(type: "INTEGER", nullable: true),
                    TunedContext = table.Column<int>(type: "INTEGER", nullable: true),
                    BenchAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelRecords_Path",
                table: "ModelRecords",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelRecords");
        }
    }
}
