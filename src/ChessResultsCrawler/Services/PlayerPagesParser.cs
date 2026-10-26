using System.Globalization;
using AngleSharp.Dom;
using System.Text.RegularExpressions;
using static ChessResultsCrawler.Services.HtmlTable;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Spielerseiten von chess-results: Spielersuche (SpielerSuche.aspx) samt Turnierteilnahmen,
/// Einzelergebnisse und „Player info"-Block der Spielerkarte (art=9).
/// Aufgerufen ueber <see cref="HtmlParserService"/>.
/// </summary>
internal static class PlayerPagesParser
{
    /// <summary>
    /// Parses the SpielerSuche.aspx player search results page.
    /// Returns list of players with Name, FideId, ChessResultsId (Ident-Number), Elo, Country, Title.
    /// </summary>
    public static async Task<List<ParsedPlayerSearchResult>> ParsePlayerSearchAsync(string html)
    {
        var results = new List<ParsedPlayerSearchResult>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Name"]);
        if (table is null) return results;

        var headers = HeaderMap(table);

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 2) continue;

            var name = GetCellValue(cells, headers, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var result = new ParsedPlayerSearchResult
            {
                Name = name,
                Title = GetCellValue(cells, headers, "Title") ?? GetCellValue(cells, headers, "Ti.") ?? GetCellValue(cells, headers, "Typ"),
                FideId = GetCellValue(cells, headers, "FideID") ?? GetCellValue(cells, headers, "FIDE-ID") ?? GetCellValue(cells, headers, "Fide-ID"),
                Country = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Fed") ?? GetCellValue(cells, headers, "Land"),
                ChessResultsId = GetCellValue(cells, headers, "Ident-Number") ?? GetCellValue(cells, headers, "Ident-Nummer") ?? GetCellValue(cells, headers, "Ident")
            };

            var eloText = GetCellValue(cells, headers, "Rtg") ?? GetCellValue(cells, headers, "Elo");
            if (int.TryParse(eloText, out var elo)) result.Elo = elo;

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Parses the SpielerSuche.aspx player search results to extract tournament participations.
    /// Returns list of tournaments with TournamentId (from tnrXXX links), TournamentName, and EndDate.
    /// </summary>
    public static async Task<List<ParsedPlayerTournament>> ParsePlayerTournamentsAsync(string html)
    {
        var results = new List<ParsedPlayerTournament>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Name"]);
        if (table is null) return results;

        var headers = HeaderMap(table);

        // Find the tournament name column index
        int tournamentColIdx = -1;
        if (headers.TryGetValue("Turnierbezeichnung", out var tbIdx)) tournamentColIdx = tbIdx;
        else if (headers.TryGetValue("Tournament", out var tIdx)) tournamentColIdx = tIdx;

        // Find the end date column index
        int endDateColIdx = -1;
        if (headers.TryGetValue("Ende-Datum", out var edIdx)) endDateColIdx = edIdx;
        else if (headers.TryGetValue("End-Date", out var edIdx2)) endDateColIdx = edIdx2;

        if (tournamentColIdx < 0) return results;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count <= tournamentColIdx) continue;

            var tournamentCell = cells[tournamentColIdx];
            var link = tournamentCell.QuerySelector("a[href]");
            if (link is null) continue;

            var href = link.GetAttribute("href") ?? "";
            var tnrMatch = Regex.Match(href, @"tnr(\d+)");
            if (!tnrMatch.Success) continue;

            var tournamentId = tnrMatch.Groups[1].Value;
            var tournamentName = link.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(tournamentName)) continue;

            string? endDate = null;
            if (endDateColIdx >= 0 && endDateColIdx < cells.Count)
            {
                var dateText = cells[endDateColIdx].TextContent.Trim();
                if (!string.IsNullOrWhiteSpace(dateText))
                    endDate = dateText;
            }

            var entry = new ParsedPlayerTournament
            {
                TournamentId = tournamentId,
                TournamentName = tournamentName,
                EndDate = endDate,
            };

            // Die Startnummer steht NUR im Link auf den Spielernamen
            // (tnr<id>.aspx?lan=1&art=9&snr=<n>) - es gibt keine Spalte dafuer. Ohne sie ist die
            // Spielerkarte dieses Turniers nicht erreichbar, und mit ihr genau EIN Abruf.
            var cardLink = row.QuerySelectorAll("a[href]")
                .Select(a => Regex.Match(a.GetAttribute("href") ?? "", @"tnr(\d+)\.aspx[^""]*[?&]snr=(\d+)"))
                .FirstOrDefault(m => m.Success);
            if (cardLink is { Success: true } && int.TryParse(cardLink.Groups[2].Value, out var snr))
                entry.Snr = snr;

            entry.IdentNumber = GetCellValue(cells, headers, "ID") ?? GetCellValue(cells, headers, "Ident-Number");
            entry.FideId = GetCellValue(cells, headers, "FideID") ?? GetCellValue(cells, headers, "Fide-ID");
            entry.Club = GetCellValue(cells, headers, "Club/City") ?? GetCellValue(cells, headers, "Verein/Ort");
            entry.Federation = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Land");
            entry.PlayerName = GetCellValue(cells, headers, "Name");

            // "-" heisst: noch nicht gespielt. Das ist die Unterscheidung zwischen einem kuenftigen
            // und einem abgeschlossenen Turnier - ein Kartenabruf lohnt nur beim zweiten.
            if (int.TryParse(GetCellValue(cells, headers, "Rk.") ?? GetCellValue(cells, headers, "Rg."), out var rank))
                entry.Rank = rank;
            if (int.TryParse(GetCellValue(cells, headers, "Rd."), out var rounds)) entry.Rounds = rounds;
            if (int.TryParse(GetCellValue(cells, headers, "n"), out var count)) entry.PlayerCount = count;

            results.Add(entry);
        }

        // Deduplicate by TournamentId
        return results
            .GroupBy(r => r.TournamentId)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Parses art=9 page (player detail / Einzelergebnisse).
    /// Columns: Rd. | Br. | Snr | Name | Elo | Land | Verein/Ort | Pkt. | Erg.
    /// Returns list of parsed results per round.
    ///
    /// <para>Die Tabelle wird ueber ihre KOPFZEILE gesucht, nicht ueber die Klasse. Auf der
    /// art=9-Seite gibt es ZWEI Tabellen mit der Klasse CRs1, und die erste ist der
    /// zweispaltige „Player info"-Block. Die frueher zuerst versuchte Klassen-Auswahl griff
    /// also den falschen Block; dessen Zeilen haben zwei Zellen, fielen durch die
    /// Mindestzellen-Pruefung und die Rundenliste kam LEER zurueck.</para>
    /// </summary>
    public static async Task<List<ParsedPlayerResult>> ParsePlayerDetailPageAsync(string html)
    {
        var results = new List<ParsedPlayerResult>();
        var document = await OpenAsync(html);

        var table = FindTableByHeaders(document, ["Rd.", "Name"])
            ?? FindTableByHeaders(document, ["Rd.", "Erg."])
            ?? document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2");
        if (table is null) return results;

        var headers = HeaderMap(table);

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 3) continue;

            var rdText = GetCellValue(cells, headers, "Rd.");
            if (!int.TryParse(rdText, out var roundNumber)) continue;

            var result = new ParsedPlayerResult { RoundNumber = roundNumber };

            var boardText = GetCellValue(cells, headers, "Br.") ?? GetCellValue(cells, headers, "Bo.");
            if (int.TryParse(boardText, out var board)) result.BoardNumber = board;

            var snrText = GetCellValue(cells, headers, "SNr") ?? GetCellValue(cells, headers, "SNo");
            if (int.TryParse(snrText, out var snr)) result.OpponentSnr = snr;

            result.OpponentName = GetCellValue(cells, headers, "Name");

            // „RtgI"/„RtgN" sind die heutigen Spaltennamen (international/national); „Rtg"/„Elo"
            // bleiben als aeltere Varianten stehen. Die INTERNATIONALE Wertung zuerst — sie ist
            // die, mit der gerechnet wird, und bei Spielern ohne nationale Wertung die einzige.
            var eloText = GetCellValue(cells, headers, "RtgI")
                          ?? GetCellValue(cells, headers, "Rtg")
                          ?? GetCellValue(cells, headers, "Elo")
                          ?? GetCellValue(cells, headers, "RtgN");
            if (int.TryParse(eloText, out var elo)) result.OpponentElo = elo;

            result.Points = GetCellValue(cells, headers, "Pkt.") ?? GetCellValue(cells, headers, "Pts.");
            result.Result = GetCellValue(cells, headers, "Erg.") ?? GetCellValue(cells, headers, "Res.");

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Der „Player info"-Block der Spielerkarte (art=9): Punkte, Platz, Performance-Rating und
    /// Elo-Aenderung eines Spielers in EINEM Turnier.
    ///
    /// <para>Das ist die einzige Quelle fuer diese vier Werte — die Spielersuche nennt nur den
    /// Platz, und die Turnierseite selbst hat sie je Spieler nur in der Tabelle, nicht als
    /// abfragbares Feld. Ein Abruf je Turnier und Spieler.</para>
    ///
    /// <para>Ein KUENFTIGES Turnier hat den Block, aber ohne Werte. Deshalb
    /// <see cref="ParsedPlayerCard.HasResult"/>: fehlen Punkte UND Platz, gab es noch kein
    /// Ergebnis — das ist der Normalfall vor dem Turnier und kein Fehler.</para>
    ///
    /// <para>Die Zahlen tragen ein Dezimal-KOMMA („1,5", „-51,6"), auch auf der englischen
    /// Seite (lan=1). Deshalb wird mit der invarianten UND der deutschen Kultur geparst.</para>
    /// </summary>
    public static async Task<ParsedPlayerCard?> ParsePlayerCardAsync(string html)
    {
        var document = await OpenAsync(html);

        // Der Block wird ueber seine ZEILEN erkannt (Beschriftung + Wert), nicht ueber die
        // Klasse: „Player info" ist eine Ueberschrift daneben, und die Klasse CRs1 tragen auf
        // dieser Seite mehrere Tabellen.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in document.QuerySelectorAll("table"))
        {
            foreach (var row in table.QuerySelectorAll(":scope > tr, :scope > tbody > tr"))
            {
                var cells = row.QuerySelectorAll(":scope > td").ToList();
                if (cells.Count != 2) continue;
                var label = cells[0].TextContent.Trim().TrimEnd(':').Trim();
                if (label.Length == 0) continue;
                fields.TryAdd(label, cells[1].TextContent.Trim());
            }
        }

        if (fields.Count == 0) return null;

        var card = new ParsedPlayerCard
        {
            Name = Field(fields, "Name", "Namen"),
            Federation = Field(fields, "Federation", "Foederation", "Land"),
            Club = Field(fields, "Club/City", "Verein/Ort"),
            IdentNumber = Field(fields, "Ident-Number", "Ident-Nummer"),
            FideId = Field(fields, "Fide-ID", "FideID"),
            StartingRank = Int(fields, "Starting rank", "Startrang"),
            RatingNational = Int(fields, "Rating national", "Elo national"),
            RatingInternational = Int(fields, "Rating international", "Elo international"),
            PerformanceRating = Int(fields, "Performance rating", "Rating-Performance", "Performance"),
            Rank = Int(fields, "Rank", "Rang", "Platz"),
            YearOfBirth = Int(fields, "Year of birth", "Geburtsjahr"),
            Points = Decimal(fields, "Points", "Punkte"),
            RatingChange = Decimal(fields, "FIDE rtg +/-", "Rtg +/-", "Elo +/-"),
        };

        // Ohne Punkte und ohne Platz ist es ein Turnier, das noch nicht gespielt wurde.
        card.HasResult = card.Points is not null || card.Rank is not null;
        card.GamesPlayed = CountGames(document);
        return card;
    }

    private static string? Field(Dictionary<string, string> fields, params string[] labels)
    {
        foreach (var label in labels)
        {
            if (fields.TryGetValue(label, out var value) && value.Length > 0) return value;
        }
        return null;
    }

    private static int? Int(Dictionary<string, string> fields, params string[] labels) =>
        int.TryParse(Field(fields, labels), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>
    /// Eine Kommazahl der Seite. chess-results schreibt „1,5" und „-51,6" — auch auf der
    /// englischen Fassung (lan=1).
    ///
    /// <para><b>Die Reihenfolge und der Stil sind beides wesentlich.</b> Mit
    /// <c>NumberStyles.Number</c> ist das Tausendertrennzeichen erlaubt, und in der invarianten
    /// Kultur IST das Komma genau das: „1,5" wird dort erfolgreich als <b>15</b> gelesen, „-51,6"
    /// als <b>-516</b>. Ein erfolgreicher Fehlwert also, den kein Fallback mehr korrigiert.
    /// Deshalb ohne <c>AllowThousands</c> und mit dem Dezimal-KOMMA zuerst: „1,5" scheitert dann
    /// invariant und gelingt deutsch, „1.5" umgekehrt.</para>
    /// </summary>
    private static decimal? Decimal(Dictionary<string, string> fields, params string[] labels)
    {
        var text = Field(fields, labels);
        if (string.IsNullOrWhiteSpace(text)) return null;

        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
                                                                 | NumberStyles.AllowLeadingWhite
                                                                 | NumberStyles.AllowTrailingWhite;

        return decimal.TryParse(text, style, GermanCulture, out var german)
            ? german
            : decimal.TryParse(text, style, CultureInfo.InvariantCulture, out var invariant) ? invariant : null;
    }

    private static readonly CultureInfo GermanCulture = new("de-DE");

    /// <summary>
    /// Wie viele Partien der Spieler in diesem Turnier WIRKLICH gespielt hat.
    ///
    /// <para>Die Rundenzahl des Turniers taugt dafuer nicht: in einer Liga steht ein Spieler an
    /// Brett 17 und wird an drei von elf Terminen aufgestellt. Gezaehlt werden deshalb die Zeilen
    /// der Partien-Tabelle unter dem Player-info-Block — und zwar nur die mit einem GEGNER: eine
    /// Zeile ohne Namen ist ein Freilos oder eine nicht gespielte Runde und keine Partie.</para>
    ///
    /// <para>Bewusst NICHT ueber Spaltenindizes: die Datenzeilen haben eine Zelle weniger als die
    /// Kopfzeile (eine leere Spalte fehlt dort), jeder feste Index zeigt also auf die falsche
    /// Spalte. Eine Zeile zaehlt, wenn sie mit einer Rundennummer beginnt und irgendwo einen
    /// Namen traegt.</para>
    /// </summary>
    private static int? CountGames(IDocument document)
    {
        var table = FindTableByHeaders(document, ["Rd.", "Name"])
            ?? FindTableByHeaders(document, ["Rd", "Name"])
            ?? FindTableByHeaders(document, ["Ru.", "Name"]);
        if (table is null) return null;

        var games = 0;
        foreach (var row in table.QuerySelectorAll("tr").Skip(1))
        {
            var cells = row.QuerySelectorAll("td, th").Select(c => c.TextContent.Trim()).ToList();
            if (cells.Count < 3) continue;
            if (!int.TryParse(cells[0], out var round) || round < 1) continue;

            // Ein Name hat Buchstaben; Zahlen, Striche und Wertungen zaehlen nicht.
            if (cells.Skip(1).Any(c => c.Length >= 3 && c.Any(char.IsLetter))) games++;
        }
        return games;
    }
}
