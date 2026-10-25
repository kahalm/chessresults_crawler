namespace ChessResultsCrawler.Services;

/// <summary>Die drei versteckten ASP.NET-Felder einer Formularseite; fehlt ein Feld oder
/// sein value-Attribut, steht dort "".</summary>
internal sealed record AspNetFormState(string ViewState, string ViewStateGenerator, string EventValidation);

/// <summary>Versteckte ASP.NET-Formularfelder der chess-results-Seiten lesen (fuer die Postbacks
/// in <see cref="ChessResultsTransport.PostBackAsync"/>).</summary>
internal static class AspNetForm
{
    /// <summary>Die drei versteckten Felder mit EINEM Parse der Seite (frueher je Postback drei
    /// volle Parses). Suche wie <see cref="ExtractHiddenField"/>.</summary>
    internal static AspNetFormState ExtractFormState(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        return new AspNetFormState(
            HiddenFieldValue(document, "__VIEWSTATE") ?? "",
            HiddenFieldValue(document, "__VIEWSTATEGENERATOR") ?? "",
            HiddenFieldValue(document, "__EVENTVALIDATION") ?? "");
    }

    /// <summary>
    /// Liest den Wert eines versteckten ASP.NET-Form-Feldes (z.B. __VIEWSTATE) per AngleSharp.
    /// Sucht zuerst über das name-Attribut, fällt auf das id-Attribut zurück. Der zurückgegebene
    /// Wert ist bereits HTML-entschlüsselt (AngleSharp dekodiert Attributwerte).
    /// Robuster gegen Markup-Drift (Attribut-Reihenfolge, Quoting, Self-Closing) als das frühere
    /// Regex. Der Parser wird je Aufruf neu erzeugt (ParseDocument ist nicht thread-safe, Crawls
    /// laufen parallel) — die Form ist klein, der Overhead vernachlässigbar.
    /// </summary>
    internal static string? ExtractHiddenField(string html, string fieldName) =>
        HiddenFieldValue(new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html), fieldName);

    private static string? HiddenFieldValue(AngleSharp.Dom.IDocument document, string fieldName)
    {
        var input = document.QuerySelector($"input[name=\"{fieldName}\"]")
                    ?? document.QuerySelector($"input[id=\"{fieldName}\"]");
        // Wie zuvor: ohne passendes Feld ODER ohne value-Attribut → null (das alte Regex
        // verlangte ein value-Attribut, sonst kein Treffer).
        return input?.GetAttribute("value");
    }
}
