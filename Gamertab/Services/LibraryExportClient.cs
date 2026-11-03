using System.Net.Http.Headers;
using Gamertab.LibraryExport;
using Google.Apis.Auth.OAuth2;

namespace Gamertab.Services;

public class LibraryExportClient(HttpClient client, IConfiguration configuration, IWebHostEnvironment environment)
{
    public async Task<string> ExportAsync(List<ExportGame> games, CancellationToken cancellationToken)
    {
        var url = configuration["LibraryExport:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new HttpRequestException("Library export is not configured.");
            return Function.Render(games, configuration["Site:BaseUrl"] ?? "https://gamertab-917646089327.us-east1.run.app");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(games) };
        var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
        var token = await credential.GetOidcTokenAsync(OidcTokenOptions.FromTargetAudience(url), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAccessTokenAsync(cancellationToken));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
