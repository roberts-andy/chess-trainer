using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChessTrainer.Web;

public sealed class AppServiceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IWebHostEnvironment environment, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (environment.IsDevelopment() && configuration.GetValue<bool>("Authentication:DevelopmentIdentity"))
        {
            var identity = new ClaimsIdentity([
                new Claim("oid", configuration["Authentication:OwnerId"] ?? ""),
                new Claim("tid", configuration["Authentication:TenantId"] ?? "")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
        // App Service strips inbound identity headers and replaces them after Easy Auth validates the cookie/token.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")))
            return Task.FromResult(AuthenticateResult.NoResult());
        string encoded = Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 65536) return Task.FromResult(AuthenticateResult.NoResult());
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var claims = document.RootElement.GetProperty("claims").EnumerateArray().Select(c =>
            {
                string type = c.GetProperty("typ").GetString()!;
                type = type switch
                {
                    "http://schemas.microsoft.com/identity/claims/objectidentifier" => "oid",
                    "http://schemas.microsoft.com/identity/claims/tenantid" => "tid",
                    _ => type
                };
                return new Claim(type, c.GetProperty("val").GetString()!);
            }).ToArray();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            new CloudAccess(configuration).Owner(principal);
            return Task.FromResult(AuthenticateResult.Success(new(principal, Scheme.Name)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Rejected an invalid or unauthorized App Service principal.");
            return Task.FromResult(AuthenticateResult.Fail("Invalid or unauthorized cloud account."));
        }
    }
}
