namespace ChessResultsCrawler.Models;

public class Tournament
{
    public int Id { get; set; }
    public required string ChessResultsId { get; set; }
    public required string Name { get; set; }
    public int TotalRounds { get; set; }
    public string? BaseUrl { get; set; }
    public string? SNode { get; set; }
    public string? Location { get; set; }
    public string? DateText { get; set; }

    /// <summary>
    /// Die Gruppen derselben Veranstaltung als JSON (<c>[{"id":"1503214","label":"Gruppe A"},…]</c>,
    /// die eigene mit ihrer Nummer), aus der Zeile „Turnierauswahl". <c>null</c> ohne Gruppen.
    /// Gespeichert statt je Abruf neu gelesen: die Zeile steht auf der Detailseite, die jeder Crawl
    /// ohnehin holt, und die Turnierseite braucht sie ohne eigenen Seitenabruf.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(4000)]
    public string? GroupsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Team> Teams { get; set; } = [];
    public ICollection<Round> Rounds { get; set; } = [];
    public ICollection<Player> Players { get; set; } = [];
    public ICollection<CrawlJob> CrawlJobs { get; set; } = [];
}
