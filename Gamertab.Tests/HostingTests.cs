using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Gamertab.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gamertab.Tests;

public class HostingTests
{
    [Fact]
    public async Task EncryptedDatabaseKeysCanRestoreProtectedDataInAnotherInstance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Gamertab test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        ServiceProvider CreateProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            services.AddDataProtection().SetApplicationName("Gamertab")
                .PersistKeysToDbContext<AppDbContext>().ProtectKeysWithCertificate(certificate);
            return services.BuildServiceProvider();
        }
        string protectedValue;
        using (var first = CreateProvider())
        {
            await using var scope = first.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.Database.EnsureCreatedAsync();
            protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("cloud-session-test").Protect("session survives a restart");
            var key = await database.DataProtectionKeys.SingleAsync();
            Assert.Contains("encryptedSecret", key.Xml);
        }
        using var second = CreateProvider();
        var restored = second.GetRequiredService<IDataProtectionProvider>().CreateProtector("cloud-session-test").Unprotect(protectedValue);
        Assert.Equal("session survives a restart", restored);
    }
}
