using AngleSharp;
using AngleSharp.Dom;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Gemeinsame Tabellen-Hilfen der chess-results-Parser: Dokument oeffnen, Datentabelle ueber
/// ihre Kopfnamen finden, Kopfzeile auf Spaltenindizes abbilden, Zelle ueber den Kopfnamen lesen.
/// </summary>
internal static class HtmlTable
{
    public static Task<IDocument> OpenAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return context.OpenAsync(req => req.Content(html));
    }

    /// <summary>
    /// Kopfname → Spaltenindex aus der ersten Zeile der Tabelle (th und td, ohne Gross-/Kleinschreibung;
    /// bei doppeltem Namen gilt die erste Spalte).
    /// </summary>
    public static Dictionary<string, int> HeaderMap(IElement table)
    {
        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }
        return headers;
    }

    /// <summary>
    /// Die Tabelle, deren erste Zeile die verlangten Kopfnamen traegt.
    ///
    /// <para><b>Bevorzugt wird eine Tabelle OHNE verschachtelte Tabelle</b> — und das ist der
    /// ganze Witz dieser Methode. chess-results baut sein Layout aus Tabellen: die Datentabelle
    /// steckt mehrere Ebenen tief (beim Rundenplan drei). <c>TextContent</c> ist REKURSIV, also
    /// „enthaelt" schon die aeusserste Wrapper-Tabelle jeden Kopfnamen, der irgendwo darin
    /// vorkommt. Ohne diese Bevorzugung kam die WRAPPER-Tabelle zurueck, deren eigene Zeilen
    /// keine Datenzeilen sind — das Ergebnis war eine leere Liste, ohne Fehler und ohne Hinweis.
    /// Auf dem Dev-Stand gemessen: 337 geprueften Turnieren standen 0 Spieltermine gegenueber.
    /// Eine Datentabelle ist immer ein BLATT.</para>
    ///
    /// <para>Findet sich kein Blatt, gilt der erste Treffer wie bisher — besser die Wrapper-
    /// Tabelle als gar nichts, falls eine Seite ihre Daten doch verschachtelt fuehrt.</para>
    /// </summary>
    public static IElement? FindTableByHeaders(IDocument document, string[] requiredHeaders)
    {
        IElement? fallback = null;

        foreach (var table in document.QuerySelectorAll("table"))
        {
            var firstRow = table.QuerySelector("tr");
            if (firstRow is null) continue;

            var headerTexts = firstRow.QuerySelectorAll("th, td")
                .Select(c => c.TextContent.Trim())
                .ToList();

            var matches = requiredHeaders.All(h =>
                headerTexts.Any(ht => ht.Contains(h, StringComparison.OrdinalIgnoreCase)));
            if (!matches) continue;

            if (table.QuerySelector("table") is null) return table;    // Blatt = Datentabelle
            fallback ??= table;
        }

        return fallback;
    }

    public static string? GetCellValue(List<IElement> cells, Dictionary<string, int> headers, string headerName)
    {
        if (headers.TryGetValue(headerName, out var idx) && idx < cells.Count)
        {
            var val = cells[idx].TextContent.Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }
}
