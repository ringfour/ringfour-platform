using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Identity;

public static class IdentityEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 200;

    public static void MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();

        group.MapGet("/me", GetMe);
        group.MapPost("/organizations", CreateOrganization);
        group.MapGet("/organizations", ListOrganizations);
        group.MapGet("/organizations/{orgId:guid}/members", ListMembers);
        group.MapPost("/organizations/{orgId:guid}/members", AddMember);
        group.MapPatch("/organizations/{orgId:guid}/members/{userId:guid}", UpdateMemberRole);
        group.MapDelete("/organizations/{orgId:guid}/members/{userId:guid}", RemoveMember);
    }

    private static string RequireSubject(HttpContext httpContext) =>
        httpContext.User.FindFirst("sub")?.Value
        ?? throw ApiException.Unauthenticated("The access token has no subject claim.");

    private static async Task<User> ResolveOrCreateUser(
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

    private static async Task<Membership> RequireMembership(
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

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static async Task<MeResponse> GetMe(
        HttpContext httpContext, RingfourDbContext db, CancellationToken cancellationToken)
    {
        var user = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        var memberships = await db.Memberships
            .Where(membership => membership.UserId == user.Id)
            .OrderBy(membership => membership.CreatedAt)
            .ThenBy(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken);
        return new MeResponse(
            UserResponse.FromEntity(user),
            memberships.Select(MembershipResponse.FromEntity).ToList());
    }

    private static async Task<IResult> CreateOrganization(
        HttpContext httpContext,
        RingfourDbContext db,
        CreateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Name is null || string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.InvalidInput("Organization name must be nonblank.");
        }
        if (request.Name.Length > 200)
        {
            throw ApiException.InvalidInput("Organization name must not exceed 200 characters.");
        }

        var user = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CreatedAt = now,
        };
        db.Organizations.Add(organization);
        db.Memberships.Add(new Membership
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = OrgRole.Owner,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/v1/organizations/{organization.Id}",
            OrganizationResponse.FromEntity(organization));
    }

    private static async Task<OrganizationListResponse> ListOrganizations(
        HttpContext httpContext,
        RingfourDbContext db,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var user = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        var (createdAfter, idAfter) = DecodeCursor(cursor);
        var pageSize = ClampLimit(limit);

        var query = db.Memberships
            .Where(membership => membership.UserId == user.Id)
            .OrderBy(membership => membership.CreatedAt)
            .ThenBy(membership => membership.OrganizationId)
            .AsQueryable();
        if (createdAfter is not null)
        {
            query = query.Where(membership =>
                membership.CreatedAt > createdAfter ||
                (membership.CreatedAt == createdAfter &&
                 membership.OrganizationId.CompareTo(idAfter!.Value) > 0));
        }

        var memberships = await query
            .Take(pageSize + 1)
            .Include(membership => membership.Organization)
            .ToListAsync(cancellationToken);

        var page = memberships.Take(pageSize).ToList();
        return new OrganizationListResponse(
            page.Select(membership => new OrganizationListItem(
                membership.OrganizationId,
                membership.Organization.Name,
                membership.Role,
                membership.Organization.CreatedAt)).ToList(),
            NextCursor(
                memberships.Count > pageSize,
                page,
                membership => membership.OrganizationId));
    }

    private static async Task<MemberListResponse> ListMembers(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var user = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        await RequireMembership(db, orgId, user.Id, OrgRole.Operator, cancellationToken);
        var (createdAfter, idAfter) = DecodeCursor(cursor);
        var pageSize = ClampLimit(limit);

        var query = db.Memberships
            .Where(membership => membership.OrganizationId == orgId)
            .OrderBy(membership => membership.CreatedAt)
            .ThenBy(membership => membership.UserId)
            .AsQueryable();
        if (createdAfter is not null)
        {
            query = query.Where(membership =>
                membership.CreatedAt > createdAfter ||
                (membership.CreatedAt == createdAfter &&
                 membership.UserId.CompareTo(idAfter!.Value) > 0));
        }

        var memberships = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
        var page = memberships.Take(pageSize).ToList();
        return new MemberListResponse(
            page.Select(MembershipResponse.FromEntity).ToList(),
            NextCursor(memberships.Count > pageSize, page, membership => membership.UserId));
    }

    private static async Task<IResult> AddMember(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        AddMemberRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        await RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

        var targetExists = await db.Users.AnyAsync(
            user => user.Id == request.UserId, cancellationToken);
        if (!targetExists)
        {
            throw ApiException.NotFound("The target user does not exist.");
        }

        var membership = new Membership
        {
            OrganizationId = orgId,
            UserId = request.UserId,
            Role = request.Role,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Memberships.Add(membership);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw ApiException.Conflict("conflict", "The user is already a member of this organization.");
        }

        return Results.Created(
            $"/v1/organizations/{orgId}/members/{membership.UserId}",
            MembershipResponse.FromEntity(membership));
    }

    private static async Task<MembershipResponse> UpdateMemberRole(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        Guid userId,
        UpdateMemberRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        await RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);

        var target = await db.Memberships.SingleOrDefaultAsync(
            membership => membership.OrganizationId == orgId && membership.UserId == userId,
            cancellationToken);
        if (target is null)
        {
            throw ApiException.NotFound("The member does not exist in this organization.");
        }

        if (target.Role == OrgRole.Owner && request.Role != OrgRole.Owner)
        {
            var owners = await CountOwners(db, orgId, cancellationToken);
            if (owners <= 1)
            {
                throw ApiException.Conflict(
                    "last_owner", "An organization must keep at least one owner.");
            }
        }

        target.Role = request.Role;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MembershipResponse.FromEntity(target);
    }

    private static async Task<IResult> RemoveMember(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var caller = await ResolveOrCreateUser(db, RequireSubject(httpContext), cancellationToken);
        await RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);

        var target = await db.Memberships.SingleOrDefaultAsync(
            membership => membership.OrganizationId == orgId && membership.UserId == userId,
            cancellationToken);
        if (target is null)
        {
            throw ApiException.NotFound("The member does not exist in this organization.");
        }

        if (target.Role == OrgRole.Owner)
        {
            var owners = await CountOwners(db, orgId, cancellationToken);
            if (owners <= 1)
            {
                throw ApiException.Conflict(
                    "last_owner", "An organization must keep at least one owner.");
            }
        }

        db.Memberships.Remove(target);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    // Serializes membership changes per organization so concurrent removals or
    // demotions cannot both pass the last-owner check.
    private static async Task LockOrganization(
        RingfourDbContext db, Guid organizationId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM organizations WHERE id = {organizationId} FOR UPDATE",
            cancellationToken);
    }

    private static async Task<int> CountOwners(
        RingfourDbContext db, Guid organizationId, CancellationToken cancellationToken) =>
        await db.Memberships.CountAsync(
            membership =>
                membership.OrganizationId == organizationId && membership.Role == OrgRole.Owner,
            cancellationToken);

    private static int ClampLimit(int? limit)
    {
        if (limit is null)
        {
            return DefaultPageSize;
        }
        if (limit < 1 || limit > MaxPageSize)
        {
            throw ApiException.InvalidInput($"limit must be between 1 and {MaxPageSize}.");
        }
        return limit.Value;
    }

    // Opaque cursor: base64url of "createdAt round-trip|tie-break id" of the
    // last item returned. Round-trip formatting preserves the exact value read
    // from PostgreSQL so equality comparisons hold.
    private static (DateTimeOffset? CreatedAt, Guid? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(cursor)));
            var parts = decoded.Split('|');
            return (
                DateTimeOffset.ParseExact(
                    parts[0], "o", System.Globalization.CultureInfo.InvariantCulture),
                Guid.Parse(parts[1]));
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            throw ApiException.InvalidInput("cursor is not valid.");
        }
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var raw = Encoding.UTF8.GetBytes($"{createdAt:o}|{id}");
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? NextCursor(
        bool hasMore, IReadOnlyList<Membership> page, Func<Membership, Guid> tieBreakId)
    {
        if (!hasMore || page.Count == 0)
        {
            return null;
        }
        var last = page[^1];
        return EncodeCursor(last.CreatedAt, tieBreakId(last));
    }

    private static string PadBase64(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
    }
}
