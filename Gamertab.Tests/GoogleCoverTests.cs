using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google;
using Google.Cloud.Storage.V1;
using Google.Apis.Download;
using Google.Apis.Upload;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gamertab.Tests;

public class GoogleCoverTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aJ0kAAAAASUVORK5CYII=");

    private class MemoryStorage : StorageClient
    {
        public Dictionary<string, byte[]> Images { get; } = [];
        public HttpStatusCode? Failure { get; set; }

        private void Check()
        {
            if (Failure is not null)
                throw new GoogleApiException("storage", "Storage error") { HttpStatusCode = Failure.Value };
        }

        public override async Task<Google.Apis.Storage.v1.Data.Object> UploadObjectAsync(string bucket, string objectName, string contentType, Stream source,
            UploadObjectOptions? options = null, CancellationToken cancellationToken = default, IProgress<IUploadProgress>? progress = null)
        {
            Check();
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, cancellationToken);
            Images.Add(objectName, copy.ToArray());
            return new() { Name = objectName, Bucket = bucket, ContentType = contentType };
        }

        public override async Task<Google.Apis.Storage.v1.Data.Object> DownloadObjectAsync(string bucket, string objectName, Stream destination, DownloadObjectOptions? options = null,
            CancellationToken cancellationToken = default, IProgress<IDownloadProgress>? progress = null)
        {
            Check();
            if (!Images.TryGetValue(objectName, out var bytes))
                throw new GoogleApiException("storage", "Missing image") { HttpStatusCode = HttpStatusCode.NotFound };
            await destination.WriteAsync(bytes, cancellationToken);
            return new() { Name = objectName, Bucket = bucket };
        }

        public override Task DeleteObjectAsync(string bucket, string objectName, DeleteObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            Check();
            if (!Images.Remove(objectName))
                throw new GoogleApiException("storage", "Missing image") { HttpStatusCode = HttpStatusCode.NotFound };
            return Task.CompletedTask;
        }
    }

    private class GoogleCoverFactory : AppFactory
    {
        public MemoryStorage Storage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BlobStorage:GoogleBucket"] = "private-covers-test"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<StorageClient>();
                services.AddSingleton<StorageClient>(Storage);
            });
        }
    }

    private static async Task<HttpClient> RegisterAsync(AppFactory factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        }
        await RefreshToken();
        var registered = await client.PostAsJsonAsync("/api/account/register", new
        {
            email = Guid.NewGuid().ToString("N") + "@example.test", password = "GamertabTest2026", confirmPassword = "GamertabTest2026"
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await RefreshToken();
        return client;
    }

    private static async Task<int> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collections", new { name = "Weekend" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int id)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Png), "cover", "original.png");
        return await client.PostAsync($"/api/collections/{id}/cover", content);
    }

    [Fact]
    public async Task GoogleCoversRemainPrivateAndReplacementAndDeletionCleanUpStorage()
    {
        using var factory = new GoogleCoverFactory();
        using var owner = await RegisterAsync(factory);
        using var stranger = await RegisterAsync(factory);
        var id = await CreateAsync(owner);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id)).StatusCode);
        var original = Assert.Single(factory.Storage.Images).Key;
        var image = await owner.GetAsync($"/api/collections/{id}/cover");
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.True(image.Headers.CacheControl?.NoStore);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/collections/{id}/cover")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(stranger, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id)).StatusCode);
        Assert.False(factory.Storage.Images.ContainsKey(original));
        Assert.Single(factory.Storage.Images);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/collections/{id}")).StatusCode);
        Assert.Empty(factory.Storage.Images);
    }

    [Fact]
    public async Task MissingGoogleImageReturns404AndAnOutageDoesNotReplaceTheExistingCover()
    {
        using var factory = new GoogleCoverFactory();
        using var owner = await RegisterAsync(factory);
        var id = await CreateAsync(owner);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id)).StatusCode);
        var original = Assert.Single(factory.Storage.Images);
        factory.Storage.Failure = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.GetAsync($"/api/collections/{id}/cover")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await UploadAsync(owner, id)).StatusCode);
        Assert.Single(factory.Storage.Images);
        factory.Storage.Failure = null;
        Assert.Equal(Png, await owner.GetByteArrayAsync($"/api/collections/{id}/cover"));
        factory.Storage.Images.Remove(original.Key);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/collections/{id}/cover")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/collections/{id}/cover")).StatusCode);
    }
}
