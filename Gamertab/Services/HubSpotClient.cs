using System.Net.Http.Headers;
using System.Text.Json;
using Gamertab.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Gamertab.Services;

public class HubSpotClient(HttpClient client, IConfiguration configuration, IMemoryCache cache)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Crm:AccessToken"]);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new HttpRequestException("Feedback is not configured.");
        using var request = new HttpRequestMessage(method, "https://api.hubapi.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["Crm:AccessToken"]);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public async Task<string> CreateAsync(FeedbackInput input, CancellationToken cancellationToken)
    {
        var content = input.Message.Trim();
        if (input.GameId.HasValue) content += "\n\nGame: " + (configuration["Site:BaseUrl"] ?? "https://gamertab-917646089327.us-east1.run.app").TrimEnd('/') + "/Games/Details/" + input.GameId;
        if (!string.IsNullOrWhiteSpace(input.ReplyEmail)) content += "\n\nReply email: " + input.ReplyEmail.Trim();
        var response = await SendAsync(HttpMethod.Post, "crm/v3/objects/tickets", new
        {
            properties = new { subject = "[Gamertab / " + input.Category + "] " + input.Subject.Trim(), content,
                hs_pipeline = configuration["Crm:PipelineId"] ?? "0", hs_pipeline_stage = configuration["Crm:NewStageId"] ?? "1" }
        }, cancellationToken);
        var id = response.GetProperty("id").GetString()!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9]{1,40}$")) throw new HttpRequestException("Invalid ticket response.");
        return id;
    }

    public async Task<(string Status, string Message)> ReadAsync(string ticketId, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "crm/v3/objects/tickets/" + Uri.EscapeDataString(ticketId) + "?properties=hs_pipeline_stage,hs_pipeline,content", null, cancellationToken);
        var properties = response.GetProperty("properties");
        var pipelines = await cache.GetOrCreateAsync("hubspot-ticket-pipelines", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await SendAsync(HttpMethod.Get, "crm/v3/pipelines/tickets", null, cancellationToken);
        });
        var pipelineId = properties.GetProperty("hs_pipeline").GetString();
        var stageId = properties.GetProperty("hs_pipeline_stage").GetString();
        var stage = pipelines.GetProperty("results").EnumerateArray().Where(pipeline => pipeline.GetProperty("id").GetString() == pipelineId)
            .SelectMany(pipeline => pipeline.GetProperty("stages").EnumerateArray()).FirstOrDefault(stage => stage.GetProperty("id").GetString() == stageId);
        var status = stage.ValueKind == JsonValueKind.Undefined ? "Status unavailable" : stage.GetProperty("label").GetString()!;
        return (status, properties.GetProperty("content").GetString() ?? "");
    }
}
