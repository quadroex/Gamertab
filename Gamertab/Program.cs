using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Gamertab.Data;
using Gamertab.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Google.Cloud.Storage.V1;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
if (args.Contains("--mcp"))
{
    await McpHost.RunAsync(args);
    return;
}
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Gamertab")));
if (builder.Configuration.GetValue<bool>("DataProtection:PersistToDatabase"))
{
    var certificate = builder.Configuration["DataProtection:Certificate"];
    if (string.IsNullOrWhiteSpace(certificate))
        throw new InvalidOperationException("Configure DataProtection:Certificate before enabling persistent keys.");
    var keyCertificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(certificate), null, X509KeyStorageFlags.EphemeralKeySet);
    builder.Services.AddDataProtection().SetApplicationName("Gamertab")
        .PersistKeysToDbContext<AppDbContext>().ProtectKeysWithCertificate(keyCertificate);
}
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireNonAlphanumeric = false;
})
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
    options.Cookie.Name = "Gamertab.Session";
});
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services.AddAuthentication().AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.ClaimActions.MapJsonKey("urn:google:email_verified", "email_verified");
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/Account/Login?externalError=true");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddMemoryCache();
builder.Services.AddOutputCache();
builder.Services.AddSingleton<GameSearchIndex>();
builder.Services.AddScoped<RecommendationService>();
builder.Services.AddScoped<GameCatalog>();
builder.Services.AddScoped<CollectionCoverService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["BlobStorage:GoogleBucket"]))
    builder.Services.AddSingleton(_ => StorageClient.Create());
builder.Services.AddScoped<PlanAccess>();
builder.Services.AddScoped<JournalStore>();
builder.Services.AddSignalR();
builder.Services.AddHttpClient<GeminiAgent>(client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient<LibraryExportClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("assistant", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddHostedService<NewsUpdateWorker>();
builder.Services.AddHttpClient<IgdbClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<SteamNewsClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<SteamStoreClient>(client => client.Timeout = TimeSpan.FromSeconds(6));

var app = builder.Build();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    app.UseExceptionHandler("/Home/Error");

app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hubs/journal") && context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseOutputCache();
app.MapGet("/health", () => Results.Ok(new { status = "ok", application = "Gamertab" }));
app.MapHub<JournalHub>("/hubs/journal");
app.MapControllerRoute("default", "{controller=Games}/{action=Index}/{id?}");

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
        await database.Database.EnsureCreatedAsync();
    else
        await database.Database.MigrateAsync();
    await StarterData.SeedAsync(database);
    scope.ServiceProvider.GetRequiredService<GameSearchIndex>().Rebuild(await database.Games.AsNoTracking().ToListAsync());
}

app.Run();

public partial class Program { }
