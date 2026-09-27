namespace ChessResultsCrawler.DTOs;

// LeagueHub (RookHub): die vier Seiten einer Mannschaftsliga, roh geparst (siehe LeaguePagesParser).

public sealed record LeagueMatchRow(int Round, int? MatchNo, string Home, string Away, double? HomePts, double? AwayPts,
    string? Date, string? Time, string? Venue);

public sealed record LeagueGameRow(int Round, int MatchNo, int Board, string HomeTeam, string AwayTeam,
    string HomePlayer, string AwayPlayer, string? HomeTitle, string? AwayTitle, string? HomeColor, string Result,
    double? HomeScore, double? AwayScore, int Forfeit, string? PgnId);

public sealed record LeagueRosterRow(int? StartNr, string? Title, string Name, string? FideId, int? EloI, int? EloN,
    string? Fed, string Team, int? RosterBoard);

public sealed record LeagueStatsRow(string Team, int? RosterBoard, string Name, double? Points, int? Games, int? EloPerf);

public sealed record LeaguePagesResponse(int Tnr, List<LeagueMatchRow> Matches, List<LeagueGameRow> Games,
    Dictionary<int, string?> RoundDates, List<LeagueRosterRow> Roster, List<LeagueStatsRow> Stats);
