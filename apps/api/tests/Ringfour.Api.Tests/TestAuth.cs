using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Ringfour.Api.Tests;

// Test-only RS256 signing material. The API must refuse this configuration
// when the host environment is Production (see StartupGuardTests).
public static class TestAuth
{
    public const string Issuer = "https://auth.test.ringfour.dev/";
    public const string Audience = "https://api.ringfour.eu";

    private static readonly RSA Rsa = RSA.Create(2048);

    public static string PrivateKeyPem => Rsa.ExportPkcs8PrivateKeyPem();

    public static string CreateToken(
        string subject,
        string? email = null,
        string? issuer = null,
        string? audience = null,
        DateTimeOffset? expires = null,
        RSA? signingKey = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        SigningCredentials credentials = algorithm == SecurityAlgorithms.HmacSha256
            ? new SigningCredentials(
                new SymmetricSecurityKey("not-the-right-key-material-0123456789"u8.ToArray()),
                algorithm)
            : new SigningCredentials(new RsaSecurityKey(signingKey ?? Rsa), algorithm);
        var claims = new List<Claim> { new("sub", subject) };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }
        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: claims,
            expires: (expires ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
