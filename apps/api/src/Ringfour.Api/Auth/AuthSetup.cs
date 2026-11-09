using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ringfour.Api.Auth;

// Two token-validation modes:
// - Auth0 (default and only hosted mode): RS256 metadata from the tenant domain.
// - Test: a PEM RSA key plus fixed issuer/audience from configuration, used by
//   component tests. Refused outright when the host environment is Production.
// ValidateAuthConfiguration runs eagerly after the host is built so invalid
// configuration fails startup; ConfigureBearer re-reads the live configuration
// so late-bound sources (e.g. test harness overrides) are honored.
public static class AuthSetup
{
    public const string TestIssuerConfigKey = "Auth:Test:Issuer";
    public const string TestAudienceConfigKey = "Auth:Test:Audience";
    public const string TestKeyConfigKey = "Auth:Test:RsaPrivateKeyPem";

    public static void AddRingfourAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
                ConfigureBearer(options, builder.Configuration, builder.Environment));
        builder.Services.AddAuthorization();
    }

    public static void ValidateAuthConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!string.IsNullOrEmpty(configuration[TestKeyConfigKey]))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Test token signing keys are test-only and must not be configured " +
                    "in a hosted (Production) environment.");
            }
            return;
        }

        if (string.IsNullOrEmpty(configuration["Auth0:Domain"]) ||
            string.IsNullOrEmpty(configuration["Auth0:Audience"]))
        {
            throw new InvalidOperationException(
                "Auth0:Domain and Auth0:Audience are required when no test signing " +
                "configuration is present.");
        }
    }

    private static void ConfigureBearer(
        JwtBearerOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        ValidateAuthConfiguration(configuration, environment);
        options.MapInboundClaims = false;

        var testKeyPem = configuration[TestKeyConfigKey];
        if (!string.IsNullOrEmpty(testKeyPem))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(testKeyPem);
            var signingKey = new RsaSecurityKey(rsa) { KeyId = "ringfour-test-key" };
            options.Configuration = new OpenIdConnectConfiguration();
            options.Configuration.SigningKeys.Add(signingKey);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration[TestIssuerConfigKey],
                ValidateAudience = true,
                ValidAudience = configuration[TestAudienceConfigKey],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = ["RS256"],
                NameClaimType = "sub",
            };
            return;
        }

        var domain = configuration["Auth0:Domain"]!;
        var audience = configuration["Auth0:Audience"]!;
        options.Authority = $"https://{domain}/";
        options.Audience = audience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://{domain}/",
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidAlgorithms = ["RS256"],
            NameClaimType = "sub",
        };
    }
}
