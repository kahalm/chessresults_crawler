using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessResultsCrawler.Migrations
{
    /// <summary>
    /// Heilt Alt-Datenbanken, deren <c>PlayerResults</c>-Tabelle noch mit
    /// <c>EnsureCreated()</c> (vor dem Player-Detail-Crawling, Commit 110effa) angelegt
    /// wurde und der daher die später ergänzten Spalten fehlen. Solche DBs haben die
    /// InitialCreate-Migration nur per manuellem <c>__EFMigrationsHistory</c>-Eintrag als
    /// "angewendet" markiert, sodass deren CreateTable mit diesen Spalten nie lief.
    /// Fehlen die Spalten, schlagen Abfragen mit
    /// <c>Unknown column 'p.OpponentElo'</c> (500er) fehl.
    ///
    /// Die ALTER-Statements sind idempotent (<c>IF NOT EXISTS</c>): auf frisch über
    /// Migrationen erzeugten Datenbanken (Spalten bereits vorhanden) sind sie No-Ops,
    /// auf Alt-DBs ergänzen sie die fehlenden Spalten. Typen entsprechen exakt
    /// InitialCreate.
    /// </summary>
    /// <inheritdoc />
    public partial class HealLegacyPlayerResultColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE `PlayerResults` " +
                "ADD COLUMN IF NOT EXISTS `OpponentSnr` int NULL, " +
                "ADD COLUMN IF NOT EXISTS `OpponentName` varchar(500) CHARACTER SET utf8mb4 NULL, " +
                "ADD COLUMN IF NOT EXISTS `OpponentElo` int NULL, " +
                "ADD COLUMN IF NOT EXISTS `Points` varchar(10) CHARACTER SET utf8mb4 NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bewusst leer: Diese Spalten gehören konzeptionell zu InitialCreate; ein
            // Rollback dieser reinen Heil-Migration darf keine Nutzdaten löschen.
        }
    }
}
