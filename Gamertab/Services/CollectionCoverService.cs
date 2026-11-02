using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Gamertab.Data;
using Gamertab.Models;
using Microsoft.EntityFrameworkCore;

namespace Gamertab.Services;

public class CollectionCoverService
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private readonly AppDbContext database;
    private readonly ILogger<CollectionCoverService> logger;
    private readonly BlobContainerClient? container;
    public bool IsConfigured => container is not null;

    public CollectionCoverService(AppDbContext database, IConfiguration configuration, ILogger<CollectionCoverService> logger)
    {
        this.database = database;
        this.logger = logger;
        var connection = configuration.GetConnectionString("BlobStorage") ?? configuration.GetConnectionString("QueueStorage");
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var options = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03);
            options.Retry.MaxRetries = 2;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            container = new BlobContainerClient(connection, configuration["BlobStorage:ContainerName"] ?? "collection-covers", options);
        }
    }

    public async Task<string> UploadAsync(GameCollection collection, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > MaxBytes)
            throw new ArgumentException("Choose a PNG or JPEG image up to 2 MB.");
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var bytes = content.GetBuffer().AsSpan(0, (int)content.Length);
        var png = bytes.Length >= 45 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8);
        var jpeg = bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217;
        if (!png && !jpeg)
            throw new ArgumentException("Choose a PNG or JPEG image. Other file types are not supported.");

        var name = Guid.NewGuid().ToString("N") + (png ? ".png" : ".jpg");
        content.Position = 0;
        await container!.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await container.GetBlobClient(name).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = png ? "image/png" : "image/jpeg" }
        }, cancellationToken);
        try
        {
            var changed = await database.Collections.Where(item => item.Id == collection.Id && item.UserId == collection.UserId && item.CoverBlobName == collection.CoverBlobName)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CoverBlobName, name), CancellationToken.None);
            if (changed == 0)
                throw new InvalidOperationException("The collection changed. Reload the page and try again.");
        }
        catch
        {
            await DeleteBlobAsync(name);
            throw;
        }
        await DeleteBlobAsync(collection.CoverBlobName);
        return name;
    }

    public async Task<BlobDownloadStreamingResult?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        try { return (await container!.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: cancellationToken)).Value; }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    public async Task RemoveAsync(GameCollection collection)
    {
        if (collection.CoverBlobName is null)
            return;
        var changed = await database.Collections.Where(item => item.Id == collection.Id && item.UserId == collection.UserId && item.CoverBlobName == collection.CoverBlobName)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CoverBlobName, (string?)null));
        if (changed == 0)
            throw new InvalidOperationException("The collection changed. Reload the page and try again.");
        await DeleteBlobAsync(collection.CoverBlobName);
    }

    public async Task DeleteCollectionAsync(GameCollection collection)
    {
        var changed = await database.Collections.Where(item => item.Id == collection.Id && item.UserId == collection.UserId && item.CoverBlobName == collection.CoverBlobName).ExecuteDeleteAsync();
        if (changed == 0)
            throw new InvalidOperationException("The collection changed. Reload the page and try again.");
        await DeleteBlobAsync(collection.CoverBlobName);
    }

    private async Task DeleteBlobAsync(string? name)
    {
        if (name is null)
            return;
        if (container is null)
        {
            logger.LogWarning("Blob storage is not configured. Cover {BlobName} was not removed.", name);
            return;
        }
        try { await container.GetBlobClient(name).DeleteIfExistsAsync(); }
        catch (RequestFailedException exception) { logger.LogWarning(exception, "Could not delete old collection cover {BlobName}.", name); }
    }
}
