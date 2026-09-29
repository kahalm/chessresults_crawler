using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die Parität des auf AngleSharp umgestellten <c>ExtractHiddenField</c> ab: gleiche
/// Eingaben → gleiche Ausgaben wie das frühere Regex, plus zusätzliche Robustheit gegen
/// Markup-Drift (Attribut-Reihenfolge, Quoting, id-Fallback).
/// </summary>
public class CrawlerServiceHiddenFieldTests
{
    [Fact]
    public void ExtractHiddenField_ReturnsValue_ForStandardAspNetInput()
    {
        var html = "<html><body><form>" +
                   "<input type=\"hidden\" name=\"__VIEWSTATE\" id=\"__VIEWSTATE\" value=\"ABC123\" />" +
                   "</form></body></html>";
        Assert.Equal("ABC123", CrawlerService.ExtractHiddenField(html, "__VIEWSTATE"));
    }

    [Fact]
    public void ExtractHiddenField_DecodesHtmlEntities()
    {
        // AngleSharp dekodiert Attributwerte automatisch (wie zuvor WebUtility.HtmlDecode).
        var html = "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"a&amp;b&lt;c\" />";
        Assert.Equal("a&b<c", CrawlerService.ExtractHiddenField(html, "__EVENTVALIDATION"));
    }

    [Fact]
    public void ExtractHiddenField_ReturnsNull_WhenFieldMissing()
    {
        var html = "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"x\" />";
        Assert.Null(CrawlerService.ExtractHiddenField(html, "__EVENTVALIDATION"));
    }

    [Fact]
    public void ExtractHiddenField_HandlesAttributeOrderAndSpacing()
    {
        // Markup-Drift: andere Attribut-Reihenfolge, zusätzliche Attribute, single quotes.
        var html = "<input value='V-with-stuff' autocomplete=\"off\" name=\"__VIEWSTATEGENERATOR\" type=\"hidden\">";
        Assert.Equal("V-with-stuff", CrawlerService.ExtractHiddenField(html, "__VIEWSTATEGENERATOR"));
    }

    [Fact]
    public void ExtractHiddenField_FallsBackToIdAttribute()
    {
        // Falls (untypisch) das name-Attribut fehlt, greift der id-Fallback.
        var html = "<input type=\"hidden\" id=\"__VIEWSTATE\" value=\"viaId\" />";
        Assert.Equal("viaId", CrawlerService.ExtractHiddenField(html, "__VIEWSTATE"));
    }

    [Fact]
    public void ExtractHiddenField_EmptyValue_ReturnsEmptyString()
    {
        var html = "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"\" />";
        Assert.Equal(string.Empty, CrawlerService.ExtractHiddenField(html, "__VIEWSTATE"));
    }

    /// <summary>
    /// Der Postback liest alle drei Felder aus EINEM Parse — mit derselben Suche wie
    /// <c>ExtractHiddenField</c> (name vor id, Entities dekodiert); fehlendes Feld oder fehlendes
    /// value wird "" (so gingen die Felder schon vorher in das Formular).
    /// </summary>
    [Fact]
    public void ExtractFormState_ReadsAllThreeFields_MissingOnesAreEmpty()
    {
        var html = "<form>" +
                   "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"a&amp;b\" />" +
                   "<input type=\"hidden\" id=\"__VIEWSTATEGENERATOR\" value=\"viaId\" />" +
                   "<input type=\"hidden\" name=\"__EVENTVALIDATION\" />" +
                   "</form>";

        var state = CrawlerService.ExtractFormState(html);

        Assert.Equal("a&b", state.ViewState);
        Assert.Equal("viaId", state.ViewStateGenerator);
        Assert.Equal("", state.EventValidation);
        Assert.Equal(new CrawlerService.AspNetFormState("", "", ""), CrawlerService.ExtractFormState("<p>kein Formular</p>"));
    }
}
