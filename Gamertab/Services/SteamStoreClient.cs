using System.Net.Http.Json;
using System.Text.Json;
using Gamertab.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Services;

public class SteamStoreClient(HttpClient http, IMemoryCache cache)
{
    public async Task<SteamStoreInfo?> GetAsync(int appId, CancellationToken cancellationToken = default)
    {
        var key = $"steam-store:us:{appId}";
        if (cache.TryGetValue<SteamStoreInfo?>(key, out var saved))
            return saved;
        JsonElement response;
        try
        {
            response = await http.GetFromJsonAsync<JsonElement>($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us&l=english", cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("Steam returned an unreadable store response.", exception);
        }
        if (response.ValueKind != JsonValueKind.Object)
            throw new HttpRequestException("Steam returned an unexpected store response.");
        if (!response.TryGetProperty(appId.ToString(), out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            cache.Set<SteamStoreInfo?>(key, null, TimeSpan.FromMinutes(2));
            return null;
        }

        var free = data.TryGetProperty("is_free", out var isFree) && isFree.ValueKind == JsonValueKind.True;
        decimal? price = null;
        decimal? original = null;
        var discount = 0;
        if (data.TryGetProperty("price_overview", out var overview) && overview.ValueKind == JsonValueKind.Object &&
            overview.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String && currency.GetString() == "USD" &&
            overview.TryGetProperty("final", out var final) && final.ValueKind == JsonValueKind.Number && final.TryGetInt32(out var cents) && cents >= 0)
        {
            price = cents / 100m;
            if (overview.TryGetProperty("initial", out var initial) && initial.ValueKind == JsonValueKind.Number && initial.TryGetInt32(out var initialCents) && initialCents > cents)
                original = initialCents / 100m;
            if (overview.TryGetProperty("discount_percent", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetInt32(out var value))
                discount = Math.Clamp(value, 0, 100);
        }
        int? metacriticScore = null;
        string? metacriticUrl = null;
        if (data.TryGetProperty("metacritic", out var critic) && critic.ValueKind == JsonValueKind.Object &&
            critic.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetInt32(out var number) && number is >= 0 and <= 100)
        {
            metacriticScore = number;
            if (critic.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
                uri.Host is "metacritic.com" or "www.metacritic.com")
                metacriticUrl = uri.AbsoluteUri;
        }
        var result = new SteamStoreInfo(free, price, original, discount, DateTimeOffset.UtcNow, metacriticScore, metacriticUrl);
        return cache.Set(key, result, TimeSpan.FromMinutes(10));
    }
}
