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

- **chessreslults_crawler** (dieses Projekt): Backend-only REST-API. Crawlt chess-results.com und speichert Turniere, Spieler, Teams, Paarungen in eigener MariaDB (`chessresults`). Kein Frontend.
- **RookHub** (`C:/git/rookhub`): Webportal mit Angular-Frontend + eigener .NET API. Leitet Turnier-Anfragen als Proxy an diesen Crawler weiter (`CrawlerProxyService` / `TournamentProxyController`). Eigene Datenbank `rookhub`.

**Wichtig**: Aenderungen an API-Endpoints hier muessen in RookHub's `src/api/RookHub.Api/Services/CrawlerProxyService.cs` und `src/api/RookHub.Api/Controllers/TournamentProxyController.cs` nachgezogen werden.

**Wichtig**: Nach jedem Feature/Fix MUSS die Version in RookHub hochgezaehlt und der Changelog gepflegt werden:
1. `version` und `changelog`-Array in `C:/git/rookhub/src/frontend/app/src/environments/environment.ts` aktualisieren (Patch fuer Fixes, Minor fuer Features)
2. `Aktuelle Version` in `C:/git/rookhub/CLAUDE.md` anpassen
3. Aenderung in RookHub committen

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

```bash
cd tests/ChessResultsCrawler.Tests
dotnet test
```
