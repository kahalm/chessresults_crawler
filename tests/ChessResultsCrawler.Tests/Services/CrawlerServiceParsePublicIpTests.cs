using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class CrawlerServiceParsePublicIpTests
{
    [Fact]
    public void ParsePublicIp_ValidJson_ReturnsIp()
    {
        var json = """{"public_ip":"141.98.102.179","country":"Germany","city":"Frankfurt"}""";
        Assert.Equal("141.98.102.179", CrawlerService.ParsePublicIp(json));
    }

    [Fact]
    public void ParsePublicIp_MissingField_ReturnsNull()
    {
        Assert.Null(CrawlerService.ParsePublicIp("""{"country":"Germany"}"""));
    }

    [Fact]
    public void ParsePublicIp_EmptyIp_ReturnsNull()
    {
        Assert.Null(CrawlerService.ParsePublicIp("""{"public_ip":""}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void ParsePublicIp_Garbage_ReturnsNull(string json)
    {
        Assert.Null(CrawlerService.ParsePublicIp(json));
    }
}
