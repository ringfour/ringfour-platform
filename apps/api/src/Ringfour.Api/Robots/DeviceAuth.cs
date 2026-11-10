using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

// Credential verification shared by /v1/device/authenticate and the
// /v1/device/connect WebSocket handshake. Identity always comes from the
// verified credential row, never from caller-supplied claims.
internal static class DeviceAuth
{
    public static async Task<(Robot Robot, RobotCredential Credential)> VerifyCredential(
        RingfourDbContext db,
        Guid credentialId,
        string? secret,
        CancellationToken cancellationToken)
    {
        if (secret is null || string.IsNullOrWhiteSpace(secret))
        {
            throw ApiException.InvalidInput("secret is required.");
        }

        var credential = await db.RobotCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (credential is null ||
            credential.RevokedAt is not null ||
            (credential.ExpiresAt is not null && credential.ExpiresAt <= now) ||
            !FixedTimeEquals(HashSecret(secret), credential.SecretHash))
        {
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        }

        var robot = await db.Robots.SingleAsync(
            candidate => candidate.Id == credential.RobotId, cancellationToken);
        return (robot, credential);
    }

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))
            .ToLowerInvariant();

    private static bool FixedTimeEquals(string leftHex, string rightHex) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(leftHex), Convert.FromHexString(rightHex));
}
