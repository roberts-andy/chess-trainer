using System.Security.Claims;
using System.Data.Common;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using ChessTrainer.Web;
using ChessTrainer.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Storage:SqlitePath"]))
    throw new InvalidOperationException("SQLite storage is only supported for local development.");
builder.Services.AddSingleton<TokenCredential>(_ =>
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTAINER_APP_JOB_NAME"))
        ? new AzureCliCredential() : new ManagedIdentityCredential(
            builder.Configuration["AZURE_CLIENT_ID"] is string clientId ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton<CloudStore>();
builder.Services.AddSingleton<CloudAccess>();
builder.Services.AddScoped<CloudTraining>();
builder.Services.AddSingleton<VisionService>();
builder.Services.AddHttpClient();
builder.Services.AddAuthentication("AppService").AddScheme<AuthenticationSchemeOptions, AppServiceAuthentication>("AppService", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.MaxBufferedUnacknowledgedRenderBatches = 10)
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 15 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 12 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("oid") ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (builder.Configuration["task"] is string task)
{
    if (task == "snapshot")
    {
        long count = LocalMigration.CreateSnapshot(
            builder.Configuration["source-db"] ?? throw new ArgumentException("Specify --source-db."),
            builder.Configuration["destination-db"] ?? throw new ArgumentException("Specify --destination-db."));
        Console.WriteLine($"Created a consistent read-only source snapshot: {count:N0} puzzles.");
        return;
    }
    var store = app.Services.GetRequiredService<CloudStore>();
    await store.InitializeAsync();
    if (task == "bootstrap")
    {
        if (builder.Configuration["Bootstrap:SourceDatabase"] is string source)
            await LocalMigration.ImportAsync(store,
                builder.Configuration["Authentication:OwnerId"] ?? throw new ArgumentException("Set the owner ID."), source);
        await store.GrantApplicationAsync(
            builder.Configuration["Bootstrap:ApplicationName"] ?? throw new ArgumentException("Set the application name."),
            Guid.Parse(builder.Configuration["Bootstrap:ClientId"] ?? throw new ArgumentException("Set the application client ID.")));
    }
    else if (task == "migrate")
        await LocalMigration.ImportAsync(store, builder.Configuration["Authentication:OwnerId"] ?? throw new InvalidOperationException("Set the owner ID."),
            builder.Configuration["source-db"] ?? throw new InvalidOperationException("Specify --source-db."),
            int.TryParse(builder.Configuration["puzzle-limit"], out int limit) && limit > 0 ? limit : null);
    else if (task == "grant")
        await store.GrantApplicationAsync(builder.Configuration["application-name"]!, Guid.Parse(builder.Configuration["client-id"]!));
    else if (task != "initialize") throw new ArgumentException("Unknown setup task.");
    Console.WriteLine($"Cloud database ready: {await store.PuzzleCountAsync():N0} puzzles.");
    return;
}
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    int status = exception switch
    {
        UnauthorizedAccessException => 403,
        KeyNotFoundException => 404,
        EditConflictException => 409,
        ArgumentException or InvalidOperationException or InvalidDataException => 400,
        _ => 500
    };
    app.Logger.Log(status == 500 ? LogLevel.Error : LogLevel.Warning, exception, "Request failed with status {Status}.", status);
    await Results.Problem(statusCode: status, detail: status == 500 ? "The operation failed. Try again; server diagnostics have been recorded." : exception.Message)
        .ExecuteAsync(context);
}));
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["X-ChessTrainer-Request"] != "1")
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Include X-ChessTrainer-Request: 1 on API writes." });
            return;
        }
    }
    await next();
});
app.UseStaticFiles();
string bundledAssets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(bundledAssets))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(bundledAssets) });
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
string? release = builder.Configuration["Release:Id"];
string? revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION");
app.MapGet("/health", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "ok", release, revision });
});
app.MapGet("/health/ready", async (CloudStore store, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await store.CheckReadyAsync(context.RequestAborted); }
    catch (Exception error) when (error is DbException or TimeoutException or AuthenticationFailedException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new { status = "ready", release, revision });
})
    .RequireRateLimiting("api");
app.MapTrainerApi();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
