using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Identity;

// Shared caller resolution and organization role checks used by every /v1
// resource module. The 404-for-non-member / 403-for-insufficient-role split
// is part of the authorization contract and must stay uniform.
internal static class IdentityAccess
{
    public static string RequireSubject(HttpContext httpContext) =>
        httpContext.User.FindFirst("sub")?.Value
        ?? throw ApiException.Unauthenticated("The access token has no subject claim.");

    public static async Task<User> ResolveOrCreateUser(
        RingfourDbContext db, string subject, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(
            existing => existing.Auth0Sub == subject, cancellationToken);
        if (user is not null)
        {
            return user;
        }

        user = new User
        {
            Id = Guid.NewGuid(),
            Auth0Sub = subject,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Concurrent first request for the same subject won the insert.
            db.Entry(user).State = EntityState.Detached;
            return await db.Users.SingleAsync(
                existing => existing.Auth0Sub == subject, cancellationToken);
        }
    }

    public static async Task<Membership> RequireMembership(
        RingfourDbContext db,
        Guid organizationId,
        Guid userId,
        OrgRole minimumRole,
        CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.SingleOrDefaultAsync(
            candidate =>
                candidate.OrganizationId == organizationId && candidate.UserId == userId,
            cancellationToken);
        if (membership is null)
        {
            throw ApiException.NotFound(
                "The requested resource does not exist in this organization.");
        }
        if (membership.Role < minimumRole)
        {
            throw ApiException.Forbidden($"This operation requires the {minimumRole} role.");
        }
        return membership;
    }

    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };
}
