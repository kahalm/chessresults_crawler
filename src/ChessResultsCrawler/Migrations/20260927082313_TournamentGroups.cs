using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessResultsCrawler.Migrations
{
    /// <inheritdoc />
    public partial class TournamentGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupsJson",
                table: "Tournaments",
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupsJson",
                table: "Tournaments");
        }
    }
}
