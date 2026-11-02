using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Gamertab.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class BlobFactAttribute : FactAttribute
{
    public BlobFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMERTAB_BLOB_TEST_CONNECTION")))
            Skip = "Run dev.ps1 blob-test to check real local Blob Storage.";
    }
}

public class CoverFactory : AppFactory
{
    public string ContainerName { get; } = "gamertab-covers-test-" + Guid.NewGuid().ToString("N");
    public BlobContainerClient Container => new(Environment.GetEnvironmentVariable("GAMERTAB_BLOB_TEST_CONNECTION"), ContainerName,
        new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:BlobStorage"] = Environment.GetEnvironmentVariable("GAMERTAB_BLOB_TEST_CONNECTION"),
            ["BlobStorage:ContainerName"] = ContainerName
        }));
    }

    public async Task<HttpClient> RegisterAsync()
    {
        var client = CreateClient(new() { AllowAutoRedirect = false });
        var session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var response = await client.PostAsJsonAsync("/api/account/register", new
        {
            email = Guid.NewGuid().ToString("N") + "@example.test", password = "GamertabTest2026", confirmPassword = "GamertabTest2026"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        session = await client.GetFromJsonAsync<JsonElement>("/api/account/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }

    public async Task<string?> BlobNameAsync(int id)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Collections.Where(item => item.Id == id).Select(item => item.CoverBlobName).SingleAsync();
    }
}

public class CollectionCoverTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aJ0kAAAAASUVORK5CYII=");

    private static async Task<int> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collections", new { name = "Weekend", coverUrl = "https://example.test/cover.png" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, byte[]? bytes = null)
    {
        var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(bytes ?? Png), "cover", "untrusted-name.png");
        return client.PostAsync($"/api/collections/{id}/cover", body);
    }

    [BlobFact]
    public async Task CoverCanBeUploadedReplacedAndRemovedWithoutChangingTheImageLink()
    {
        using var factory = new CoverFactory();
        try
        {
            using var client = await factory.RegisterAsync();
            var id = await CreateAsync(client);
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, id)).StatusCode);
            var original = await factory.BlobNameAsync(id);
            var image = await client.GetAsync($"/api/collections/{id}/cover");
            Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
            Assert.True(image.Headers.CacheControl?.NoStore);
            Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, id)).StatusCode);
            Assert.NotEqual(original, await factory.BlobNameAsync(id));
            Assert.False((await factory.Container.GetBlobClient(original).ExistsAsync()).Value);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/collections/{id}/cover")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/collections/{id}/cover")).StatusCode);
            var collection = await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
            Assert.Equal("https://example.test/cover.png", collection.GetProperty("coverUrl").GetString());
            Assert.Equal(JsonValueKind.Null, collection.GetProperty("uploadedCoverUrl").ValueKind);
            Assert.Empty(await factory.Container.GetBlobsAsync().ToListAsync());
        }
        finally { await factory.Container.DeleteIfExistsAsync(); }
    }

    [BlobFact]
    public async Task PrivateCoversAreOnlyAccessibleToTheirOwner()
    {
        using var factory = new CoverFactory();
        try
        {
            using var owner = await factory.RegisterAsync();
            var id = await CreateAsync(owner);
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id)).StatusCode);
            using var other = await factory.RegisterAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/collections/{id}/cover")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other, id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/collections/{id}/cover")).StatusCode);
            using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/collections/{id}/cover")).StatusCode);
            using var direct = new HttpClient();
            var blobUrl = factory.Container.GetBlobClient(await factory.BlobNameAsync(id)).Uri;
            Assert.Contains((await direct.GetAsync(blobUrl)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/collections/{id}/cover")).StatusCode);
        }
        finally { await factory.Container.DeleteIfExistsAsync(); }
    }

    [BlobFact]
    public async Task InvalidAndOversizedFilesLeaveTheExistingCoverIntact()
    {
        using var factory = new CoverFactory();
        try
        {
            using var client = await factory.RegisterAsync();
            var id = await CreateAsync(client);
            await UploadAsync(client, id);
            var original = await factory.BlobNameAsync(id);
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, id, Encoding.UTF8.GetBytes("<svg><script>bad</script></svg>"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, id, new byte[2 * 1024 * 1024 + 1])).StatusCode);
            Assert.Equal(original, await factory.BlobNameAsync(id));
            Assert.Equal(Png, await client.GetByteArrayAsync($"/api/collections/{id}/cover"));
            Assert.Single(await factory.Container.GetBlobsAsync().ToListAsync());
        }
        finally { await factory.Container.DeleteIfExistsAsync(); }
    }

    [BlobFact]
    public async Task DeletingACollectionFromTheApiOrPageRemovesItsBlob()
    {
        using var factory = new CoverFactory();
        try
        {
            using var client = await factory.RegisterAsync();
            for (var source = 0; source < 2; source++)
            {
                var id = await CreateAsync(client);
                await UploadAsync(client, id);
                var blob = factory.Container.GetBlobClient(await factory.BlobNameAsync(id));
                var response = source == 0 ? await client.DeleteAsync($"/api/collections/{id}") :
                    await client.PostAsync($"/Collections/Delete/{id}", new FormUrlEncodedContent([]));
                Assert.Equal(source == 0 ? HttpStatusCode.NoContent : HttpStatusCode.Redirect, response.StatusCode);
                Assert.False((await blob.ExistsAsync()).Value);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/collections/{id}")).StatusCode);
            }
        }
        finally { await factory.Container.DeleteIfExistsAsync(); }
    }
}
