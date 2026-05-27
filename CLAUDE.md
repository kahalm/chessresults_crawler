# ChessResults Crawler

Spezialisierter Web-Crawler der Turnierdaten von chess-results.com extrahiert. Gehoert zusammen mit **RookHub** (`C:/git/rookhub`) – bei Aenderungen immer beide Projekte beruecksichtigen.

## Zusammenspiel der Projekte

```
RookHub Frontend (Angular :8085)
    |
RookHub API (.NET :5001)  -- proxy -->  Crawler API (.NET :8080)  -- crawl -->  chess-results.com
    |                                        |
    v                                        v
  rookhub DB (MariaDB)                 chessresults DB (MariaDB)
```

- **chessresults_crawler** (dieses Projekt): Backend-only REST-API. Crawlt chess-results.com und speichert Turniere, Spieler, Teams, Paarungen in eigener MariaDB (`chessresults`). Kein Frontend.
- **RookHub** (`C:/git/rookhub`): Webportal mit Angular-Frontend + eigener .NET API. Leitet Turnier-Anfragen als Proxy an diesen Crawler weiter (`CrawlerProxyService` / `TournamentProxyController`). Eigene Datenbank `rookhub`.

**Wichtig**: Aenderungen an API-Endpoints hier muessen in RookHub's `src/api/RookHub.Api/Services/CrawlerProxyService.cs` und `src/api/RookHub.Api/Controllers/TournamentProxyController.cs` nachgezogen werden.

**Wichtig – Checkliste vor JEDEM Commit (beide Projekte)**:
1. [ ] Tests vorhanden fuer die Aenderung?
2. [ ] `version` und `changelog`-Array in `C:/git/rookhub/src/frontend/app/src/environments/environment.ts` aktualisiert? (Patch fuer Fixes, Minor fuer Features)
3. [ ] `Aktuelle Version` in `C:/git/rookhub/CLAUDE.md` angepasst?
4. [ ] Versionsaenderung in RookHub committet?
5. [ ] **Nach jedem Commit dem User die aktuelle Version mitteilen** (z.B. "Version: 0.6.6")

**NIEMALS committen ohne diese Checkliste abzuarbeiten.** Auch reine Test- oder Doku-Aenderungen erhoehen die Patch-Version.

## Tech Stack

| Komponente | Technologie | Version |
|-----------|-------------|---------|
| Runtime | .NET | 9.0 |
| Web Framework | ASP.NET Core Web API | 9.0 |
| ORM | EF Core + Pomelo | 9.0.0 |
| Datenbank | MariaDB | 11 |
| HTML-Parsing | AngleSharp | 1.4.0 |
| API Docs | Swashbuckle (Swagger) | 10.1.7 |
| VPN (Produktion) | Gluetun (WireGuard) | 1.0 |
| Tests | xUnit 2.9.2 + Moq 4.20.72 | - |

## API-Endpoints

| Methode | Endpoint | Zweck |
|---------|----------|-------|
| POST | `/api/crawl` | Crawl starten (Body: `{ chessResultsId, jobType }`) |
| GET | `/api/crawl/{jobId}` | Job-Status abfragen |
| GET | `/api/tournaments` | Alle Turniere auflisten |
| GET | `/api/tournaments/{id}` | Turnierdetails |
| GET | `/api/tournaments/{id}/players?team=&sortBy=` | Spieler (filterbar nach Team, sortierbar nach elo/name/board/snr) |
| GET | `/api/tournaments/{id}/teams` | Teams auflisten |
| GET | `/api/tournaments/{id}/teams/{snr}` | Team-Details mit Spielern |
| GET | `/api/tournaments/{id}/pairings?round=` | Paarungen (optional nach Runde filtern) |
| GET | `/api/tournaments/{id}/pairings/latest` | Paarungen der letzten Runde |
| GET | `/api/tournaments/{id}/rounds` | Alle Runden |
| GET | `/api/tournaments/{id}/rounds/check` | Neue Runden erkennen (`{ knownRounds, availableRounds, hasNewRound, newRoundNumbers }`) |
| GET | `/api/health` | Health Check |
| GET | `/api/health/ip` | Aktuelle IP (VPN-Verifizierung) |

### CrawlJob Types
- `Full` – Komplett (Spieler + Paarungen)
- `PlayersOnly` – Nur Spielerdaten
- `PairingsOnly` – Nur Paarungen/Ergebnisse
- `CheckNewRounds` – Nur pruefen ob neue Runden publiziert wurden

## Datenbank-Schema

| Tabelle | Zweck | Wichtige Felder |
|---------|-------|----------------|
| Tournaments | Turnier-Metadaten | ChessResultsId (unique), Name, TotalRounds, BaseUrl, SNode |
| Teams | Mannschaften | TournamentId, Snr (unique pro Turnier), Name |
| Players | Einzelspieler | TournamentId, TeamId, Name, Title, FideId, Elo, Country, BoardNumber, Snr |
| Rounds | Turnierrunden | TournamentId, RoundNumber (unique pro Turnier), PairingsPublished, ResultsPublished |
| TeamPairings | Mannschaftspaarungen | RoundId, MatchNumber, HomeTeamId, AwayTeamId, HomeScore, AwayScore |
| PlayerResults | Einzelergebnisse | RoundId, PlayerId, BoardNumber, Result |
| CrawlJobs | Job-Tracking | TournamentId, ChessResultsId, JobType, Status, ErrorMessage |

## Projektstruktur

```
src/ChessResultsCrawler/
  Controllers/             CrawlController, HealthController, TournamentsController
  Services/                CrawlerService (Rate Limiting, HTTP), HtmlParserService (AngleSharp),
                           TournamentService (DB-Queries), RoundDetectionService
  Models/                  Tournament, Team, Player, Round, TeamPairing, PlayerResult, CrawlJob
  DTOs/                    CrawlDtos (CrawlRequest, CrawlJobResponse), TournamentDtos
  Data/                    AppDbContext (alle Relationships + Indexes)
  Program.cs               Startup-Konfiguration, Auto-Migration
tests/ChessResultsCrawler.Tests/
  Services/                HtmlParserServiceTests, TournamentServiceTests
  Models/                  EntityModelTests
  DTOs/                    DtoMappingTests
```

## Crawler-Implementierungsdetails

- **Rate Limiting**: Statisches `SemaphoreSlim`, 1500ms Minimum zwischen HTTP-Requests
- **SNode-Erkennung**: Folgt Redirects von chess-results.com um den Server (s1/s2/s3) zu ermitteln
- **Flexibles Parsing**: Header-Matching toleriert Variationen (z.B. "Nr."/"Snr", "Rtg"/"Elo", "Fed"/"FED")
- **Score-Formate**: Parst "3,5:0,5", "3.5:0.5", "3:1"
- **Hintergrund-Jobs**: Async-Ausfuehrung mit eigenem Service-Scope, Status-Tracking in CrawlJobs-Tabelle
- **Upsert-Logik**: Re-Crawl ueberschreibt bestehende Paarungen pro Runde

## EF Core Migrations

Die Datenbank nutzt EF Core Migrations (statt `EnsureCreated()`). Auto-Migration ist in `Program.cs` aktiv.

```bash
cd src/ChessResultsCrawler
dotnet ef migrations add <MigrationName>    # Nutzt DesignTimeDbContextFactory
dotnet ef database update                   # Braucht laufende MariaDB
```

### Upgrade von EnsureCreated auf Migrate (einmalig)

Bestehende Datenbanken, die mit `EnsureCreated()` angelegt wurden, haben eine leere `__EFMigrationsHistory`-Tabelle. Beim ersten Start mit `Migrate()` versucht die InitialCreate-Migration alle Tabellen neu anzulegen und schlaegt fehl ("Table already exists").

**Fix**: Vor dem ersten Start mit Migrations muss die InitialCreate-Migration manuell als "angewendet" markiert werden:

```sql
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20260527165801_InitialCreate', '9.0.0');
```

Danach startet der Crawler normal und wendet nur zukuenftige Migrations an.

## Lokales Development

Der komplette Stack (Crawler + RookHub) wird ueber RookHub's Compose-Dateien gestartet:
```bash
cd C:/git/rookhub
# Development (ohne VPN):
docker compose -f compose.dev.yml --env-file .env.dev up --build

# Production (mit Gluetun VPN):
docker compose -f compose.vpn.yml --env-file .env.vpn up --build
```
Crawler-Swagger: http://localhost:8080/swagger/ui/index.html

Standalone (nur Crawler + eigene DB, braucht VPN-Config in .env):
```bash
docker compose up --build
```

## Tests

**Pflicht**: Jedes neue Feature, jeder neue Endpoint und jeder Bugfix MUSS mit mindestens einem Test abgedeckt werden. Kein PR/Commit ohne passenden Test.

```bash
cd tests/ChessResultsCrawler.Tests
dotnet test
```

### Test-Pattern
- **InMemory DB** pro Testklasse via `UseInMemoryDatabase(Guid.NewGuid().ToString())`
- **IDisposable** fuer DB-Cleanup
- **xUnit `[Fact]`** / `[Theory]` Attribute
- **Namenskonvention**: `MethodName_Scenario_ExpectedResult`
- **Service-Tests** testen direkt gegen InMemory-DB (kein Mocking noetig)
- **Controller mit Inline-Logik** (z.B. CrawlController, RequestLogController) werden direkt gegen DB getestet
- **Parser-Tests** (HtmlParserService) brauchen keine DB, HTML wird als String uebergeben

### Teststruktur
```
tests/ChessResultsCrawler.Tests/
  Services/                HtmlParserServiceTests, HtmlParserServiceExtendedTests,
                           TournamentServiceTests, TournamentServiceExtendedTests
  Controllers/             CrawlControllerTests, RequestLogQueryTests
  Models/                  EntityModelTests
  DTOs/                    DtoMappingTests
```
