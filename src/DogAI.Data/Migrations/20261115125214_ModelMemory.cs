using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DogAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MeasuredVramBytes",
                table: "ModelRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MeasuredVramContext",
                table: "ModelRecords",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MeasuredVramBytes",
                table: "ModelRecords");

            migrationBuilder.DropColumn(
                name: "MeasuredVramContext",
                table: "ModelRecords");
        }
    }
}
