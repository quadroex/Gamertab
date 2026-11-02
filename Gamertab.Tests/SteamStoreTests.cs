using System.Net;
using System.Text;
using Gamertab.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Tests;

public class SteamStoreTests
{
    private class StoreHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task DiscountedUsdPricesAreReadFromSteamAndCached()
    {
        using var handler = new StoreHandler("""{"620":{"success":true,"data":{"is_free":false,"price_overview":{"currency":"USD","initial":1999,"final":499,"discount_percent":75}}}}""");
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SteamStoreClient(http, cache);
        var result = await service.GetAsync(620);
        Assert.Equal(4.99m, result!.Price);
        Assert.Equal(19.99m, result.OriginalPrice);
        Assert.Equal(75, result.DiscountPercent);
        Assert.False(result.IsFree);
        Assert.Contains("cc=us", handler.LastRequest!.Query);
        Assert.Same(result, await service.GetAsync(620));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("{\"is_free\":true}", true)]
    [InlineData("{\"is_free\":false}", false)]
    [InlineData("{\"is_free\":false,\"price_overview\":{\"currency\":\"UAH\",\"final\":19900}}", false)]
    public async Task FreeMissingAndNonUsdPricesAreNotInvented(string data, bool isFree)
    {
        using var handler = new StoreHandler("{\"620\":{\"success\":true,\"data\":" + data + "}}");
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await new SteamStoreClient(http, cache).GetAsync(620);
        Assert.Equal(isFree, result!.IsFree);
        Assert.Null(result.Price);
        Assert.Null(result.MetacriticScore);
    }

    [Theory]
    [InlineData(95, "https://www.metacritic.com/game/portal-2/", true)]
    [InlineData(80, "https://example.test/untrusted", false)]
    [InlineData(101, "https://www.metacritic.com/game/portal-2/", false)]
    public async Task MetacriticScoresAreOptionalAndLinksMustPointToTheSource(int score, string url, bool hasLink)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { is_free = false, metacritic = new { score, url } });
        using var handler = new StoreHandler("{\"620\":{\"success\":true,\"data\":" + body + "}}");
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await new SteamStoreClient(http, cache).GetAsync(620);
        Assert.Equal(score <= 100 ? score : (int?)null, result!.MetacriticScore);
        Assert.Equal(hasLink, result.MetacriticUrl is not null);
    }

    [Fact]
    public async Task AnUnavailableListingIsCachedWithoutAFabricatedOffer()
    {
        using var handler = new StoreHandler("""{"620":{"success":false}}""");
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SteamStoreClient(http, cache);
        Assert.Null(await service.GetAsync(620));
        Assert.Null(await service.GetAsync(620));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("Service unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("Not JSON", HttpStatusCode.OK)]
    [InlineData("[]", HttpStatusCode.OK)]
    public async Task StoreFailuresCanBeHandledWithoutBreakingGameDetails(string body, HttpStatusCode status)
    {
        using var handler = new StoreHandler(body, status);
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await Assert.ThrowsAsync<HttpRequestException>(() => new SteamStoreClient(http, cache).GetAsync(620));
    }
}
