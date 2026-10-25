namespace ChessResultsCrawler.Services;

/// <summary>
/// Zustandslose Abfragen gegen chess-results — nichts wird gespeichert: Vereinsnamen, Turnierkopf,
/// Turniersuche, Ankuendigungskalender, Spielersuche, Rundenplan, Spielerkarte, Partiedatenbank.
/// Alle ueber <see cref="ChessResultsTransport"/>; oeffentlich ueber <see cref="CrawlerService"/>.
/// </summary>
internal sealed class ChessResultsQueries
{
    private readonly ChessResultsTransport _transport;
    private readonly HtmlParserService _parser;

    public ChessResultsQueries(ChessResultsTransport transport, HtmlParserService parser)
    {
        _transport = transport;
        _parser = parser;
    }

    /// <summary>
    /// Die Vereins-/Mannschaftsnamen eines Turniers — EIN Seitenabruf, nichts wird gespeichert.
    ///
    /// <para>Gedacht als Hinweisgeber fuer die Verortung in RookHub: der Spielort steht dort als
    /// Freitext und oft abgekuerzt, die Vereinsnamen tragen die Unterscheidung mit („SV - Das
    /// Wien - St.Veit/Glan" statt bloss „St.Veit"). Einzelturniere liefern eine leere Liste, das
    /// ist der Normalfall und kein Fehler.</para>
    /// </summary>
    public async Task<List<string>> FetchTeamNamesAsync(string chessResultsId, CancellationToken ct = default)
    {
        var html = await _transport.FetchPageAsync($"https://chess-results.com/tnr{chessResultsId}.aspx", "lan=1", ct);
        return await _parser.ParseTeamNamesAsync(html);
    }

    /// <summary>
    /// Kopfdaten EINES Turniers, ohne es zu importieren: Termin, Ort, Rundenzahl und die
    /// BEDENKZEIT. Ein Seitenabruf (art=0 mit turdet=YES — nur diese Ansicht traegt die
    /// Turnierdetails), zustandslos wie die Rundenplan- und Vereins-Abfrage.
    ///
    /// <para>Gebraucht fuer den Turnierverlauf: die Spielersuche liefert Termin, Platz und
    /// Rundenzahl, aber KEINE Bedenkzeit — und ohne die laesst sich eine Performance im Blitz
    /// nicht von einer im Turnierschach trennen, obwohl beide Zahlen dieselbe Einheit haben.</para>
    /// </summary>
    public async Task<ParsedTournamentInfo> FetchTournamentInfoAsync(
        string chessResultsId, CancellationToken ct = default)
    {
        // Die Turnierdetails stehen NICHT im GET. chess-results blendet sie bei Turnieren, die
        // laenger als fuenf Tage vorbei sind, hinter einem Knopf aus („To reduce the server load
        // by daily scanning of all links … all links for tournaments older than 5 days are shown
        // after clicking the following button") — dahinter steckt ein ASP.NET-Postback auf
        // `cb_alleDetails`. Ohne diesen zweiten Schritt liefert die Seite die Startrangliste und
        // sonst nichts; genau daran lieferte der Endpunkt anfangs ueberall `null`.
        var page = await _transport.PostBackAsync(
            $"https://chess-results.com/tnr{chessResultsId}.aspx?lan=1&art=0&turdet=YES",
            form => new Dictionary<string, string>
            {
                ["__EVENTTARGET"] = "",
                ["__EVENTARGUMENT"] = "",
                ["__VIEWSTATE"] = form.ViewState,
                ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
                ["__EVENTVALIDATION"] = form.EventValidation,
                ["cb_alleDetails"] = "Show tournament details",
            }, ct);

        var html = page.Body;
        var details = await _parser.ParseTournamentDetailsAsync(html);
        return new ParsedTournamentInfo
        {
            ChessResultsId = chessResultsId,
            // Der Name steht schon im ersten Abruf — und dort verlaesslicher (die Detailseite
            // wiederholt ihn nicht immer).
            Name = await _parser.ParseTournamentNameAsync(page.FormHtml),
            DateText = details.DateText,
            Location = details.Location,
            TimeControl = details.TimeControl,
            TimeControlKind = details.TimeControlKind,
            TotalRounds = await _parser.ParseTotalRoundsAsync(html),
        };
    }

    /// <summary>
    /// Fragt die chess-results-Turniersuche (TurnierSuche.aspx) fuer eine Foederation und ein
    /// Zeitfenster ab — ein Postback (<see cref="ChessResultsTransport.PostBackAsync"/>).
    ///
    /// lan=1 (englisch) ist bewusst gesetzt: die Trefferliste liefert dann Datumsangaben als
    /// "yyyy/MM/dd" statt "dd.MM.yyyy". Der Parser kann beides, aber ein festes Format haelt die
    /// Fixtures ehrlich.
    ///
    /// Der Datumsfilter der Suche greift auf das END-Datum eines Turniers, nicht auf den Beginn -
    /// ein im Fenster endendes Langzeitturnier taucht also auch dann auf, wenn es davor begann.
    ///
    /// <para><paramref name="art"/> ist die chess-results-Turnierart:
    /// "5" alle (Vorgabe), "0" Schweizer System, "1" Rundenturnier, "2" Rundenturnier fuer
    /// MANNSCHAFTEN, "3" Schweizer System fuer MANNSCHAFTEN. Damit laesst sich Einzel gegen
    /// Mannschaft aus der Quelle beantworten, statt es am Turniernamen zu raten.</para>
    /// </summary>
    public async Task<List<ParsedDirectoryTournament>> SearchTournamentsAsync(
        string federation, DateOnly from, DateOnly to, int maxRows = 2000, string art = "5",
        CancellationToken ct = default)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var page = await _transport.PostBackAsync("https://chess-results.com/TurnierSuche.aspx?lan=1", form => new Dictionary<string, string>
        {
            ["__EVENTTARGET"] = "",
            ["__EVENTARGUMENT"] = "",
            ["__LASTFOCUS"] = "",
            ["__VIEWSTATE"] = form.ViewState,
            ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
            ["__EVENTVALIDATION"] = form.EventValidation,
            ["ctl00$P1$combo_land"] = federation,
            ["ctl00$P1$txt_von_tag"] = from.ToString("dd.MM.yyyy", invariant),
            ["ctl00$P1$txt_bis_tag"] = to.ToString("dd.MM.yyyy", invariant),
            ["ctl00$P1$combo_art"] = art,        // 5 = alle Turnierarten, 2/3 = Mannschaften
            ["ctl00$P1$combo_bedenkzeit"] = "0", // alle Bedenkzeiten
            ["ctl00$P1$combo_sort"] = "3",       // nach Start-Datum
            ["ctl00$P1$combo_anzahl_zeilen"] = RowCountOption(maxRows),
            ["ctl00$P1$txt_bez"] = "",
            ["ctl00$P1$txt_ort"] = "",
            ["ctl00$P1$txt_veranstalter"] = "",
            ["ctl00$P1$txt_leiter"] = "",
            ["ctl00$P1$txt_Schiedsrichter"] = "",
            ["ctl00$P1$txt_Hauptschiedsrichter"] = "",
            ["ctl00$P1$txt_tnr"] = "",
            ["ctl00$P1$txt_eventid"] = "",
            ["ctl00$P1$cb_suchen"] = "Search",
        }, ct);

        var results = await _parser.ParseTournamentSearchAsync(page.Body);
        return maxRows > 0 && results.Count > maxRows ? results.Take(maxRows).ToList() : results;
    }

    /// <summary>
    /// Der ANKUENDIGUNGS-Kalender (<c>Kalender.aspx</c>) — der zweite Datenbestand von
    /// chess-results, und ein anderer als die Turniersuche.
    ///
    /// <para><b>Warum es beide gibt.</b> Die Turniersuche fuellt sich, wenn der Veranstalter seine
    /// Swiss-Manager-Datei hochlaedt — typisch Tage bis Wochen vor dem Turnier. Der Kalender wird
    /// vom Veranstalter VORAB gepflegt. Fuer AUT am 2026-09-07 gemessen: die Turniersuche kennt 8
    /// im November beginnende Turniere und 7 im Dezember, der Kalender 23 und 16. Von 143
    /// kuenftigen Kalendereintraegen fehlten <b>93</b> in der Turniersuche.</para>
    ///
    /// <para><b>Ein Abruf genuegt fuer alles.</b> Die Laenderauswahl kennt neben 16 Foederationen
    /// den Wert <c>-</c> („All entries"), und der liefert sie zusammen: 209 kuenftige Eintraege,
    /// davon AUT 143, GER 21, SUI 21, CZE 7, ITA 4, POL 3. Der Kalender ist also KEIN Ersatz fuer
    /// die Turniersuche (die deckt 261 Foederationen ab), sondern ein Vorlauf-Zusatz fuer die
    /// Laender, die ihn benutzen.</para>
    ///
    /// <para>Der Ablauf ist derselbe ASP.NET-Postback wie bei der Turniersuche: GET fuer die
    /// versteckten Felder, dann POST mit <c>__EVENTTARGET</c> auf die Laenderauswahl — es ist ein
    /// Auswahl-Postback, kein Suchknopf.</para>
    /// </summary>
    /// <param name="federation">Dreibuchstabiger Code, oder <c>-</c> fuer alle.</param>
    public async Task<List<ParsedCalendarEntry>> FetchCalendarAsync(
        string federation = "-", CancellationToken ct = default)
    {
        var page = await _transport.PostBackAsync("https://chess-results.com/Kalender.aspx?lan=1", form => new Dictionary<string, string>
        {
            // Die Laenderauswahl loest den Postback aus; ein Suchknopf existiert hier nicht.
            ["__EVENTTARGET"] = "ctl00$P1$combo_landsel$DropDownList1",
            ["__EVENTARGUMENT"] = "",
            ["__VIEWSTATE"] = form.ViewState,
            ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
            ["__EVENTVALIDATION"] = form.EventValidation,
            ["ctl00$P1$combo_landsel$DropDownList1"] = federation,
            // 0 = alle Kategorien (2 Jugend, 3 Senioren, 5 Frauen).
            ["ctl00$P1$combo_kat$DropDownList1"] = "0",
        }, ct);

        return await _parser.ParseCalendarAsync(page.Body);
    }

    /// <summary>
    /// Das Zeilenlimit der Suche ist ein Dropdown-INDEX, kein Zahlenwert: 0=100, 1=250, 2=500,
    /// 3=1000, 4=1500, 5=2000. Gewaehlt wird die kleinste Stufe, die maxRows noch abdeckt.
    /// </summary>
    internal static string RowCountOption(int maxRows)
    {
        int[] steps = [100, 250, 500, 1000, 1500, 2000];
        for (var i = 0; i < steps.Length; i++)
        {
            if (maxRows <= steps[i]) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return (steps.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<ParsedPlayerSearchResult>> SearchPlayersAsync(string lastName, string? firstName, CancellationToken ct = default)
    {
        // Kein __EVENTTARGET/__EVENTARGUMENT: die Spielersuche lief nie damit (s. PostBackAsync).
        var page = await _transport.PostBackAsync("https://chess-results.com/SpielerSuche.aspx?lan=0", form => new Dictionary<string, string>
        {
            ["__VIEWSTATE"] = form.ViewState,
            ["__EVENTVALIDATION"] = form.EventValidation,
            ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
            ["ctl00$P1$txt_nachname"] = lastName,
            ["ctl00$P1$txt_vorname"] = firstName ?? "",
            ["ctl00$P1$cb_suchen"] = "Suchen"
        }, ct);
        var results = await _parser.ParsePlayerSearchAsync(page.Body);

        // Deduplicate: SpielerSuche returns one row per tournament per player
        // Prefer entries with a real ChessResultsId (not "0" or empty)
        var deduplicated = results
            .GroupBy(r => r.Name)
            .Select(g => g.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ChessResultsId) && r.ChessResultsId != "0") ?? g.First())
            .Take(50)
            .ToList();

        return deduplicated;
    }

    /// <summary>
    /// Der Rundenplan eines Turniers (art=14) — je Runde Nummer, Datum und Uhrzeit. Zustandslos,
    /// ein Seitenabruf; art=14 ist die kleinste Ansicht, die die Termine traegt (17 kB gegen
    /// 33 kB bei art=2 und 182 kB bei art=3).
    ///
    /// <para>Eine leere Liste heisst „kein Rundenplan hinterlegt" — bei vielen Turnieren der
    /// Normalfall und kein Fehler.</para>
    /// </summary>
    public async Task<List<ParsedRoundDate>> FetchRoundPlanAsync(
        string chessResultsId, CancellationToken ct = default)
    {
        var html = await _transport.FetchPageAsync(
            $"https://chess-results.com/tnr{chessResultsId}.aspx", "lan=1&art=14", ct);
        return await _parser.ParseRoundPlanAsync(html);
    }

    /// <summary>
    /// Die Spielerkarte EINES Spielers in EINEM Turnier (art=9) — Punkte, Platz,
    /// Performance-Rating und Elo-Aenderung. Zustandslos, ein Seitenabruf.
    ///
    /// <para>Die Startnummer kommt aus der Spielersuche
    /// (<see cref="SearchPlayerTournamentsAsync"/>); ohne sie ist die Karte nicht adressierbar.
    /// <c>null</c> heisst „die Seite hat keinen Player-info-Block" — bei einer falschen
    /// Startnummer der Normalfall.</para>
    /// </summary>
    public async Task<ParsedPlayerCard?> FetchPlayerCardAsync(
        string chessResultsId, int snr, CancellationToken ct = default)
    {
        var html = await _transport.FetchPageAsync(
            $"https://chess-results.com/tnr{chessResultsId}.aspx", $"lan=1&art=9&snr={snr}", ct);
        return await _parser.ParsePlayerCardAsync(html);
    }

    public async Task<List<ParsedPlayerTournament>> SearchPlayerTournamentsAsync(string lastName, string? firstName, CancellationToken ct = default)
    {
        // lan=1 (englisch): der Parser sucht die Spalten ueber ihre KOPFZEILEN, und die
        // englische Fassung liefert zusaetzlich Datumsangaben als "yyyy/MM/dd".
        var page = await _transport.PostBackAsync("https://chess-results.com/SpielerSuche.aspx?lan=1", form => new Dictionary<string, string>
        {
            ["__VIEWSTATE"] = form.ViewState,
            ["__EVENTVALIDATION"] = form.EventValidation,
            ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
            ["ctl00$P1$txt_nachname"] = lastName,
            ["ctl00$P1$txt_vorname"] = firstName ?? "",
            ["ctl00$P1$cb_suchen"] = "Search"
        }, ct);
        var results = await _parser.ParsePlayerTournamentsAsync(page.Body);

        // Deduplicate and limit
        return results
            .GroupBy(r => r.TournamentId)
            .Select(g => g.First())
            .Take(50)
            .ToList();
    }

    /// <summary>
    /// chess-results-Partiedatenbank (<c>PartieSuche.aspx</c>): alle Partien eines Spielers als PGN,
    /// gesucht über die FIDE-ID (LeagueHub). Ablauf wie die Turniersuche: GET für die versteckten
    /// Felder, dann POST mit dem Download-Knopf. Die Knopf-Beschriftung ist der WERT, den ASP.NET
    /// erwartet („Download als PGN-Datei", deutsche Maske) — ein anderer Wert endet in einem
    /// „Laufzeitfehler". Ohne Partien antwortet die Seite mit HTML statt PGN → leerer String.
    /// </summary>
    public async Task<string> SearchGamesPgnByFideAsync(string fideId, CancellationToken ct = default)
    {
        var page = await _transport.PostBackAsync("https://chess-results.com/PartieSuche.aspx?lan=0", form => new Dictionary<string, string>
        {
            ["__EVENTTARGET"] = "",
            ["__EVENTARGUMENT"] = "",
            ["__LASTFOCUS"] = "",
            ["__VIEWSTATE"] = form.ViewState,
            ["__VIEWSTATEGENERATOR"] = form.ViewStateGenerator,
            ["__EVENTVALIDATION"] = form.EventValidation,
            ["ctl00$P1$Txt_FideID"] = fideId,
            ["ctl00$P1$txt_nachname"] = "",
            ["ctl00$P1$txt_vorname"] = "",
            ["ctl00$P1$Txt_NatID"] = "",
            ["ctl00$P1$txt_bez"] = "",
            ["ctl00$P1$txt_dbkey"] = "",
            ["ctl00$P1$txt_rdvon"] = "",
            ["ctl00$P1$txt_rdbis"] = "",
            ["ctl00$P1$txt_von_tag"] = "",
            ["ctl00$P1$txt_bis_tag"] = "",
            ["ctl00$P1$combo_spielerfarbe"] = "-",
            ["ctl00$P1$combo_ergebnis"] = "-",
            ["ctl00$P1$combo_anzahl_zeilen"] = "5",   // Index 5 = 2000 Zeilen
            ["ctl00$P1$cb_DownLoadPGN"] = "Download als PGN-Datei",
        }, ct);
        var type = page.MediaType ?? "";
        return type.Contains("html", StringComparison.OrdinalIgnoreCase) || !page.Body.Contains("[Event ") ? "" : page.Body;
    }
}
