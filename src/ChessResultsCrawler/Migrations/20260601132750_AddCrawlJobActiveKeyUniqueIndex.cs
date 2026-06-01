using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessResultsCrawler.Migrations
{
    /// <inheritdoc />
    public partial class AddCrawlJobActiveKeyUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveKey",
                table: "CrawlJobs",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true,
                computedColumnSql: "CASE WHEN `Status` IN (0,1) THEN `ChessResultsId` ELSE NULL END",
                stored: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CrawlJobs_ActiveKey",
                table: "CrawlJobs",
                column: "ActiveKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CrawlJobs_ActiveKey",
                table: "CrawlJobs");

            migrationBuilder.DropColumn(
                name: "ActiveKey",
                table: "CrawlJobs");
        }
    }
}
