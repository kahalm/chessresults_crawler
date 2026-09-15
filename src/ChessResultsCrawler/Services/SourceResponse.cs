using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Eine Quelle hat GEANTWORTET — aber nicht mit dem, was sie liefern soll. Typisch eine Sperr-
/// oder Warteseite, die statt der erwarteten JSON-Liste HTML zurueckgibt, oft sogar mit HTTP 200.
///
/// <para>Ohne diese Unterscheidung starb der Abruf im JSON-Parser
/// (<c>JsonReaderException: '&lt;' is an invalid start of a value</c>) und der Endpunkt antwortete
/// mit einer unbehandelten 500. Im naechtlichen Log stand damit nur ein Parser-Stacktrace — kein
/// Hinweis darauf, dass die Quelle eine Sperrseite ausliefert.</para>
/// </summary>
public class SourceResponseException : Exception
{
    public string Source { get; }
    public int StatusCode { get; }
    public string? ContentType { get; }

    /// <summary>Der Anfang der Antwort, Leerraum zusammengezogen, hoechstens <see cref="SourceResponse.ExcerptLength"/> Zeichen.</summary>
    public string Excerpt { get; }

    public SourceResponseException(string source, int statusCode, string? contentType, string excerpt)
        : base($"{source}: Antwort ist kein JSON (HTTP {statusCode}, {contentType ?? "ohne Content-Type"}) — Anfang: {excerpt}")
    {
        Source = source;
        StatusCode = statusCode;
        ContentType = contentType;
        Excerpt = excerpt;
    }
}

public static class SourceResponse
{
    internal const int ExcerptLength = 200;

    /// <summary>
    /// Wirft, wenn der Rumpf nicht mit <c>[</c> oder <c>{</c> beginnt. Entschieden wird am
    /// INHALT, nicht am Content-Type: eine Sperrseite kann sich als JSON ausgeben, und eine
    /// brauchbare Antwort mit falschem Kopf waere trotzdem lesbar.
    /// </summary>
    public static void EnsureJson(string source, int statusCode, string? contentType, string body)
    {
        var start = (body ?? "").AsSpan().TrimStart();
        if (start.Length > 0 && (start[0] == '[' || start[0] == '{')) return;
        throw new SourceResponseException(source, statusCode, contentType, ExcerptOf(body));
    }

    internal static string ExcerptOf(string? body)
    {
        var collapsed = Regex.Replace(body ?? "", @"\s+", " ").Trim();
        return collapsed.Length <= ExcerptLength ? collapsed : collapsed[..ExcerptLength];
    }
}
