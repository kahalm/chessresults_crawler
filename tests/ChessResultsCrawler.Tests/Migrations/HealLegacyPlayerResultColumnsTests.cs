using System.Reflection;
using ChessResultsCrawler.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ChessResultsCrawler.Tests.Migrations;

/// <summary>
/// Sichert die Heil-Migration ab, die Alt-DBs (vor Player-Detail-Crawling per
/// EnsureCreated angelegt) die fehlenden PlayerResults-Spalten ergänzt. Da die
/// Tests gegen die InMemory-Provider laufen (kein echtes ALTER), wird stattdessen
/// das von der Migration erzeugte SQL geprüft.
/// </summary>
public class HealLegacyPlayerResultColumnsTests
{
    private static string BuildUpSql()
    {
        var migration = new HealLegacyPlayerResultColumns();
        var builder = new MigrationBuilder("Pomelo.EntityFrameworkCore.MySql");

        // Up ist protected -> per Reflection aufrufen.
        typeof(HealLegacyPlayerResultColumns)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, new object[] { builder });

        var sqlOps = builder.Operations.OfType<SqlOperation>().ToList();
        Assert.NotEmpty(sqlOps);
        return string.Join("\n", sqlOps.Select(o => o.Sql));
    }

    [Theory]
    [InlineData("OpponentSnr")]
    [InlineData("OpponentName")]
    [InlineData("OpponentElo")]
    [InlineData("Points")]
    public void Up_AddsMissingPlayerResultColumn(string column)
    {
        var sql = BuildUpSql();
        Assert.Contains($"ADD COLUMN IF NOT EXISTS `{column}`", sql);
    }

    [Fact]
    public void Up_TargetsPlayerResultsTable()
    {
        Assert.Contains("ALTER TABLE `PlayerResults`", BuildUpSql());
    }

    [Fact]
    public void Up_IsIdempotent_UsesIfNotExists()
    {
        // Jede hinzugefügte Spalte muss IF NOT EXISTS verwenden, damit die Migration
        // auf frisch migrierten DBs (Spalten vorhanden) ein No-Op bleibt.
        var sql = BuildUpSql();
        Assert.DoesNotContain("ADD COLUMN `", sql); // kein nicht-idempotentes ADD COLUMN
        Assert.Equal(4, CountOccurrences(sql, "ADD COLUMN IF NOT EXISTS"));
    }

    [Fact]
    public void Down_IsNoOp()
    {
        var migration = new HealLegacyPlayerResultColumns();
        var builder = new MigrationBuilder("Pomelo.EntityFrameworkCore.MySql");
        typeof(HealLegacyPlayerResultColumns)
            .GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, new object[] { builder });
        Assert.Empty(builder.Operations);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
