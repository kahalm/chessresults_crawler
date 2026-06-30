using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Testet die Inline-Logik des TournamentsController gegen die InMemory-DB:
/// Paging-Clamping und die ID-Auflösung (numerische Id ODER ChessResultsId).
/// RoundDetectionService wird für diese Pfade nicht benötigt (null!).
/// </summary>
public class TournamentsControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly TournamentsController _ctrl;

    public TournamentsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _ctrl = new TournamentsController(new TournamentService(_db), null!);
    }

    public void Dispose() => _db.Dispose();

    private static int IntProp(object value, string name) =>
        (int)value.GetType().GetProperty(name)!.GetValue(value)!;

    [Theory]
    [InlineData(-5, 9999, 1, 200)]   // page < 1 → 1, pageSize > 200 → 200
    [InlineData(0, 0, 1, 1)]         // page < 1 → 1, pageSize < 1 → 1
    [InlineData(3, 50, 3, 50)]       // gültige Werte bleiben
    public async Task GetAll_ClampsPagingParameters(int page, int pageSize, int expPage, int expSize)
    {
        var ok = Assert.IsType<OkObjectResult>(await _ctrl.GetAll(page, pageSize));
        Assert.Equal(expPage, IntProp(ok.Value!, "page"));
        Assert.Equal(expSize, IntProp(ok.Value!, "pageSize"));
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await _ctrl.GetById("999"));
    }

    [Fact]
    public async Task GetById_ResolvesByNumericPrimaryKey()
    {
        var t = new Tournament { ChessResultsId = "100", Name = "Numeric" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _ctrl.GetById(t.Id.ToString()));
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetById_ResolvesByChessResultsId_WhenNotANumericPrimaryKey()
    {
        _db.Tournaments.Add(new Tournament { ChessResultsId = "abc123", Name = "ByCrId" });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _ctrl.GetById("abc123"));
        Assert.NotNull(ok.Value);
    }
}
