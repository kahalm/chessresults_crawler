using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.DTOs;

public class TournamentResponse
{
    public int Id { get; set; }
    public string ChessResultsId { get; set; } = "";
    public string Name { get; set; } = "";
    public int TotalRounds { get; set; }
    public int KnownRounds { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static TournamentResponse FromEntity(Tournament t) => new()
    {
        Id = t.Id,
        ChessResultsId = t.ChessResultsId,
        Name = t.Name,
        TotalRounds = t.TotalRounds,
        KnownRounds = t.Rounds?.Count ?? 0,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt
    };
}

public class PlayerResponse
{
    public int Id { get; set; }
    public int Snr { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? FideId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? TeamName { get; set; }
    public int? BoardNumber { get; set; }

    public static PlayerResponse FromEntity(Player p) => new()
    {
        Id = p.Id,
        Snr = p.Snr,
        Name = p.Name,
        Title = p.Title,
        FideId = p.FideId,
        Elo = p.Elo,
        Country = p.Country,
        TeamName = p.Team?.Name,
        BoardNumber = p.BoardNumber
    };
}

public class TeamResponse
{
    public int Id { get; set; }
    public int Snr { get; set; }
    public string Name { get; set; } = "";
    public List<PlayerResponse> Players { get; set; } = [];

    public static TeamResponse FromEntity(Team t, bool includePlayers = false) => new()
    {
        Id = t.Id,
        Snr = t.Snr,
        Name = t.Name,
        Players = includePlayers
            ? t.Players.Select(PlayerResponse.FromEntity).ToList()
            : []
    };
}

public class TeamPairingResponse
{
    public int Id { get; set; }
    public int RoundNumber { get; set; }
    public int MatchNumber { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public decimal? HomeScore { get; set; }
    public decimal? AwayScore { get; set; }

    public static TeamPairingResponse FromEntity(TeamPairing tp) => new()
    {
        Id = tp.Id,
        RoundNumber = tp.Round?.RoundNumber ?? 0,
        MatchNumber = tp.MatchNumber,
        HomeTeam = tp.HomeTeam?.Name ?? "",
        AwayTeam = tp.AwayTeam?.Name ?? "",
        HomeScore = tp.HomeScore,
        AwayScore = tp.AwayScore
    };
}

public class PairingResponse
{
    public int Id { get; set; }
    public int RoundNumber { get; set; }
    public int BoardNumber { get; set; }
    public string White { get; set; } = "";
    public string Black { get; set; } = "";
    public string? Result { get; set; }

    public static PairingResponse FromEntity(Pairing p) => new()
    {
        Id = p.Id,
        RoundNumber = p.Round?.RoundNumber ?? 0,
        BoardNumber = p.BoardNumber,
        White = p.WhitePlayer?.Name ?? "",
        Black = p.BlackPlayer?.Name ?? "",
        Result = p.Result
    };
}

public class RoundResponse
{
    public int Id { get; set; }
    public int RoundNumber { get; set; }
    public bool PairingsPublished { get; set; }
    public bool ResultsPublished { get; set; }

    public static RoundResponse FromEntity(Round r) => new()
    {
        Id = r.Id,
        RoundNumber = r.RoundNumber,
        PairingsPublished = r.PairingsPublished,
        ResultsPublished = r.ResultsPublished
    };
}
