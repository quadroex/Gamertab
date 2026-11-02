using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gamertab.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Services;

public class IgdbClient(HttpClient http, IConfiguration configuration, IMemoryCache cache)
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset nextRequestAt;
    private const string Fields = "name,summary,cover.url,genres.name,themes.name,game_modes.name,platforms.name,player_perspectives.name,first_release_date,rating,aggregated_rating,similar_games,external_games.uid,external_games.external_game_source.name,age_ratings.organization.name,age_ratings.rating_category.rating";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Igdb:ClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["Igdb:ClientSecret"]);

    public async Task<List<Game>> BrowseAsync(string? query, string? genre, int page, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return [];
        var filter = "game_type = (0,4,8,9,10,11) & version_parent = null";
        if (!string.IsNullOrEmpty(genre) && CatalogPage.Genres.TryGetValue(genre, out var genreId))
            filter += $" & genres = ({genreId})";
        var search = string.IsNullOrWhiteSpace(query)
            ? "sort rating_count desc;"
            : $"search {JsonSerializer.Serialize(query.Trim())};";
        if (string.IsNullOrWhiteSpace(query))
            filter += " & rating_count >= 20";
        var data = await SendAsync("games", $"{search} fields {Fields}; where {filter}; limit 13; offset {(page - 1) * 12};", cancellationToken);
        return data.EnumerateArray().Select(ParseGame).ToList();
    }

    public async Task<List<Game>> GetManyAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var selectedIds = ids.Where(id => id > 0).Distinct().Take(40).ToArray();
        if (!IsConfigured || selectedIds.Length == 0)
            return [];
        var data = await SendAsync("games", $"fields {Fields}; where id = ({string.Join(',', selectedIds)}); limit 40;", cancellationToken);
        return data.EnumerateArray().Select(ParseGame).ToList();
    }

    public async Task<Game?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var game = (await GetManyAsync([id], cancellationToken)).SingleOrDefault();
        if (game is null)
            return null;
        var data = await SendAsync("game_time_to_beats", $"fields hastily,normally,completely,count; where game_id = {id}; limit 1;", cancellationToken);
        if (data.GetArrayLength() > 0)
        {
            var time = data[0];
            game.MainHours = Hours(time, "hastily");
            game.ExtrasHours = Hours(time, "normally");
            game.CompleteHours = Hours(time, "completely");
            game.TimeSubmissions = time.TryGetProperty("count", out var count) ? count.GetInt32() : null;
        }
        return game;
    }

    public async Task<GameMedia> GetMediaAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || id <= 0)
            return new GameMedia([], null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var data = await SendAsync("games", $"fields screenshots.image_id,videos.name,videos.video_id; where id = {id}; limit 1;", timeout.Token);
        if (data.GetArrayLength() == 0)
            return new GameMedia([], null);
        var game = data[0];
        var screenshots = new List<GameScreenshot>();
        if (game.TryGetProperty("screenshots", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                var imageId = Text(image, "image_id");
                if (Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,100}$"))
                    screenshots.Add(new GameScreenshot($"https://images.igdb.com/igdb/image/upload/t_screenshot_med/{imageId}.jpg",
                        $"https://images.igdb.com/igdb/image/upload/t_1080p/{imageId}.jpg"));
            }
        }
        GameVideo? video = null;
        if (game.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            video = videos.EnumerateArray().Select(item => new GameVideo(Text(item, "video_id"), Text(item, "name")))
                .Where(item => Regex.IsMatch(item.YouTubeId, "^[A-Za-z0-9_-]{11}$"))
                .OrderByDescending(item => item.Title.Contains("trailer", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Title.Contains("launch", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        return new GameMedia(screenshots.Distinct().Take(6).ToList(), video);
    }

    public async Task<List<Game>> FindRelatedAsync(IReadOnlyList<Game> favourites, CancellationToken cancellationToken = default)
    {
        static string[] Values(IEnumerable<string> values) => values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().Take(4).ToArray();
        var genres = Values(favourites.Select(game => game.Genres)).Where(value => value is not ("Adventure" or "Indie")).ToArray();
        var themes = Values(favourites.Select(game => game.Themes)).Where(value => value != "Action").ToArray();
        var perspectives = Values(favourites.Select(game => game.Perspectives));
        if (!IsConfigured || (genres.Length == 0 && themes.Length == 0))
            return [];
        var filter = "version_parent = null & game_type = (0,8,9) & rating_count >= 5";
        if (genres.Length > 0)
            filter += " & genres.name = (" + string.Join(',', genres.Select(value => JsonSerializer.Serialize(value))) + ")";
        if (themes.Length > 0)
            filter += " & themes.name = (" + string.Join(',', themes.Select(value => JsonSerializer.Serialize(value))) + ")";
        if (perspectives.Length > 0)
            filter += " & player_perspectives.name = (" + string.Join(',', perspectives.Select(value => JsonSerializer.Serialize(value))) + ")";
        var data = await SendAsync("games", $"fields {Fields}; where {filter}; sort rating desc; limit 30;", cancellationToken);
        return data.EnumerateArray().Select(ParseGame).ToList();
    }

    private async Task<JsonElement> SendAsync(string endpoint, string query, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("IGDB credentials are not configured.");
        var cacheKey = "igdb:" + endpoint + ":" + query;
        if (cache.TryGetValue<JsonElement>(cacheKey, out var cached))
            return cached;

        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var clientId = configuration["Igdb:ClientId"]!;
                var tokenKey = "igdb-token:" + clientId;
                if (!cache.TryGetValue<string>(tokenKey, out var token))
                {
                    using var tokenResponse = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["client_secret"] = configuration["Igdb:ClientSecret"]!,
                        ["grant_type"] = "client_credentials"
                    }), cancellationToken);
                    tokenResponse.EnsureSuccessStatusCode();
                    using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
                    token = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
                    var expiresIn = tokenDocument.RootElement.GetProperty("expires_in").GetInt32();
                    cache.Set(tokenKey, token, TimeSpan.FromSeconds(Math.Max(30, expiresIn - 60)));
                }

                var delay = nextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/" + endpoint);
                request.Headers.Add("Client-ID", clientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(query, Encoding.UTF8, "text/plain");
                nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(300);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    cache.Remove(tokenKey);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var result = document.RootElement.Clone();
                cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
                return result;
            }
            throw new HttpRequestException("IGDB authentication failed.");
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static Game ParseGame(JsonElement data)
    {
        var game = new Game
        {
            IgdbId = data.GetProperty("id").GetInt32(),
            Name = Text(data, "name"),
            Summary = Text(data, "summary"),
            Genres = Names(data, "genres"),
            Themes = Names(data, "themes"),
            Modes = Names(data, "game_modes"),
            Platforms = Names(data, "platforms"),
            Perspectives = Names(data, "player_perspectives"),
            UserRating = Number(data, "rating"),
            CriticRating = Number(data, "aggregated_rating"),
            DataSource = "IGDB"
        };
        if (data.TryGetProperty("first_release_date", out var released))
            game.ReleasedOn = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(released.GetInt64()).UtcDateTime);
        if (data.TryGetProperty("cover", out var cover))
        {
            var url = Text(cover, "url");
            game.CoverUrl = (url.StartsWith("//") ? "https:" + url : url).Replace("t_thumb", "t_cover_big");
        }
        if (data.TryGetProperty("similar_games", out var similar))
            game.SimilarIgdbIds = string.Join(',', similar.EnumerateArray().Select(item => item.GetInt32()));
        if (data.TryGetProperty("external_games", out var external))
        {
            foreach (var item in external.EnumerateArray())
            {
                if (item.TryGetProperty("external_game_source", out var source) &&
                    source.ValueKind == JsonValueKind.Object && Text(source, "name").Equals("Steam", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(Text(item, "uid"), out var steamId))
                {
                    game.SteamAppId = steamId;
                    break;
                }
            }
        }
        if (data.TryGetProperty("age_ratings", out var ratings))
        {
            var labels = new List<string>();
            foreach (var item in ratings.EnumerateArray())
            {
                if (item.TryGetProperty("organization", out var organization) && organization.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("rating_category", out var category) && category.ValueKind == JsonValueKind.Object)
                {
                    var name = Text(organization, "name");
                    if (name is "PEGI" or "ESRB")
                        labels.Add(name + " " + Text(category, "rating"));
                }
            }
            game.AgeRatings = string.Join(" · ", labels.Distinct());
        }
        return game;
    }

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double? Number(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static double? Hours(JsonElement item, string property)
    {
        var seconds = Number(item, property);
        return seconds > 0 ? Math.Round(seconds.Value / 3600, 1) : null;
    }

    private static string Names(JsonElement data, string property) =>
        data.TryGetProperty(property, out var values) ? string.Join(',', values.EnumerateArray().Select(item => Text(item, "name"))) : "";
}
