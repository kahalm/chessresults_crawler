# ChessResults Crawler

Spezialisierter Web-Crawler der Turnierdaten von [chess-results.com](https://chess-results.com) extrahiert und als REST-API bereitstellt. Backend-only, kein Frontend — wird von [RookHub](../rookhub) als Datenquelle genutzt.

💬 **Community / Fragen?** Komm in unseren Discord: https://discord.gg/nKQCdC7Xff

## Architektur

```
RookHub API (.NET :5001)
    │ Crawler__BaseUrl
    ▼
Crawler API (.NET :8080)  ──crawl──>  chess-results.com
    │                                    (AngleSharp HTML-Parsing)
    ▼
MariaDB (:3306)
  DB: chessresults
```

Im VPN-Modus (Produktion) wird der ausgehende Traffic durch Gluetun/WireGuard getunnelt:

```
Crawler  ──>  Gluetun (WireGuard)  ──>  chess-results.com
```

## Tech Stack

| Komponente | Technologie | Version |
|-----------|-------------|---------|
| Runtime | .NET | 9.0 |
| Web Framework | ASP.NET Core Web API | 9.0 |
| ORM | EF Core + Pomelo (MySQL) | 9.0 |
| Datenbank | MariaDB | 11 |
| HTML-Parsing | AngleSharp | 1.4 |
| API Docs | Swagger / Swashbuckle | 10.1 |
| VPN (optional) | Gluetun (WireGuard) | - |
| Tests | xUnit + Moq | 2.9 / 4.20 |

## Voraussetzungen

- [Docker](https://docs.docker.com/get-docker/) + Docker Compose
- Fuer den vollen Stack: [RookHub](../rookhub) als Sibling-Verzeichnis:
  ```
  git/
    rookhub/
    chessresults_crawler/   # dieses Repo
  ```

## Schnellstart

### Empfohlen: Ueber RookHub starten (kompletter Stack)

```bash
cd ../rookhub

# Development (ohne VPN):
docker compose -f compose.dev.yml --env-file .env.dev up --build

# Production (mit VPN):
docker compose -f compose.vpn.yml --env-file .env.vpn up --build
```

### Standalone (nur Crawler + eigene DB)

```bash
# .env aus Vorlage erstellen und VPN-Keys eintragen
cp .env.example .env

docker compose up --build
```

**Hinweis:** Die Standalone-Konfiguration (`docker-compose.yml`) nutzt immer Gluetun/VPN.

### Zugriff

| Dienst | URL |
|--------|-----|
| Crawler Swagger | http://localhost:8080/swagger/ui/index.html |
| Health Check | http://localhost:8080/api/health |
| VPN-IP pruefen | http://localhost:8080/api/health/ip |

## API-Endpoints

### Crawl-Jobs

| Methode | Endpoint | Zweck |
|---------|----------|-------|
| POST | `/api/crawl` | Crawl starten |
| GET | `/api/crawl/{jobId}` | Job-Status abfragen |

**Request-Body fuer POST `/api/crawl`:**
```json
{
  "chessResultsId": "tur123456",
  "jobType": "Full"
}
```

**Job-Types:**
| Typ | Beschreibung |
|-----|-------------|
| `Full` | Komplett: Spieler + Paarungen aller Runden |
| `PlayersOnly` | Nur Spieler-/Teamdaten |
| `PairingsOnly` | Nur Paarungen/Ergebnisse |
| `CheckNewRounds` | Nur pruefen ob neue Runden publiziert wurden |

### Turniere

| Methode | Endpoint | Zweck |
|---------|----------|-------|
| GET | `/api/tournaments` | Alle Turniere auflisten |
| GET | `/api/tournaments/{id}` | Turnierdetails |
| GET | `/api/tournaments/{id}/players?team=&sortBy=` | Spieler (filterbar, sortierbar) |
| GET | `/api/tournaments/{id}/teams` | Teams auflisten |
| GET | `/api/tournaments/{id}/teams/{snr}` | Team-Details mit Spielern |
| GET | `/api/tournaments/{id}/pairings?round=` | Paarungen (optional nach Runde) |
| GET | `/api/tournaments/{id}/pairings/latest` | Paarungen der letzten Runde |
| GET | `/api/tournaments/{id}/rounds` | Alle Runden |
| GET | `/api/tournaments/{id}/rounds/check` | Neue Runden erkennen |

**Sortieroptionen fuer `/players`:** `elo`, `name`, `board`, `snr`

### Health

| Methode | Endpoint | Zweck |
|---------|----------|-------|
| GET | `/api/health` | Health Check |
| GET | `/api/health/ip` | Aktuelle oeffentliche IP (VPN-Verifizierung) |

## Datenbank-Schema

```
Tournaments ─┬── Teams ──── Players
              ├── Rounds ─┬── TeamPairings
              │            └── PlayerResults
              └── CrawlJobs
```

| Tabelle | Zweck | Schluessel |
|---------|-------|-----------|
| **Tournaments** | Turnier-Metadaten | `ChessResultsId` (unique) |
| **Teams** | Mannschaften | `TournamentId` + `Snr` (unique) |
| **Players** | Einzelspieler | `TournamentId`, `TeamId`, `Snr` |
| **Rounds** | Turnierrunden | `TournamentId` + `RoundNumber` (unique) |
| **TeamPairings** | Mannschaftspaarungen | `RoundId`, `MatchNumber` |
| **PlayerResults** | Einzelergebnisse | `RoundId`, `PlayerId` |
| **CrawlJobs** | Job-Tracking + Status | `TournamentId`, `Status` |

## Crawler-Details

### Rate Limiting
- `SemaphoreSlim` mit 1500 ms Minimum zwischen HTTP-Requests
- Verhindert Ueberlastung von chess-results.com

### SNode-Erkennung
- chess-results.com nutzt mehrere Server (s1, s2, s3)
- Der Crawler folgt Redirects um den korrekten Server zu ermitteln
- SNode wird pro Turnier gespeichert

### HTML-Parsing (AngleSharp)
- Flexibles Header-Matching: toleriert Variationen wie "Nr."/"Snr", "Rtg"/"Elo", "Fed"/"FED"
- Score-Formate: parst "3,5:0,5", "3.5:0.5", "3:1"

### Hintergrund-Jobs
- Crawl-Jobs laufen asynchron mit eigenem Service-Scope
- Status-Tracking ueber `CrawlJobs`-Tabelle (`Pending` → `Running` → `Completed`/`Failed`)
- Re-Crawl ueberschreibt bestehende Paarungen pro Runde (Upsert-Logik)

## Entwicklung

### Tests

```bash
cd tests/ChessResultsCrawler.Tests
dotnet test
```

### Standalone Build

```bash
cd src/ChessResultsCrawler
dotnet build
dotnet run    # braucht MariaDB auf localhost:3306
```

## Projektstruktur

```
chessresults_crawler/
  docker-compose.yml          Standalone mit Gluetun VPN
  src/ChessResultsCrawler/
    Controllers/              CrawlController, HealthController, TournamentsController
    Services/
      CrawlerService          HTTP-Requests + Rate Limiting
      HtmlParserService       AngleSharp HTML-Parsing
      TournamentService       DB-Queries
      RoundDetectionService   Neue-Runden-Erkennung
    Models/                   Tournament, Team, Player, Round, TeamPairing, PlayerResult, CrawlJob
    DTOs/                     CrawlRequest, CrawlJobResponse, TournamentDtos
    Data/                     AppDbContext (Relationships + Indexes)
    Program.cs                Startup, Auto-Migration
    Dockerfile
  tests/ChessResultsCrawler.Tests/
    Services/                 HtmlParserServiceTests, TournamentServiceTests
    Models/                   EntityModelTests
    DTOs/                     DtoMappingTests
```

## Zusammenspiel mit RookHub

RookHub leitet Turnier-Anfragen als Proxy an diesen Crawler weiter. Aenderungen an API-Endpoints muessen in folgenden RookHub-Dateien nachgezogen werden:

- `src/api/RookHub.Api/Services/CrawlerProxyService.cs`
- `src/api/RookHub.Api/Controllers/TournamentProxyController.cs`

## Lizenz

Privates Projekt — kein oeffentliches Repository.
