using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Identity;

public static class IdentityEndpoints
{
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

    private static async Task<MeResponse> GetMe(
        HttpContext httpContext, RingfourDbContext db, CancellationToken cancellationToken)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
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

        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
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
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        var (createdAfter, idAfter) = Pagination.DecodeCursor(cursor);
        var pageSize = Pagination.ClampLimit(limit);

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
            Pagination.NextCursor(
                memberships.Count > pageSize,
                page,
                membership => membership.CreatedAt,
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
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Operator, cancellationToken);
        var (createdAfter, idAfter) = Pagination.DecodeCursor(cursor);
        var pageSize = Pagination.ClampLimit(limit);

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
            Pagination.NextCursor(
                memberships.Count > pageSize,
                page,
                membership => membership.CreatedAt,
                membership => membership.UserId));
    }

    private static async Task<IResult> AddMember(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        AddMemberRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

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
        catch (DbUpdateException exception) when (IdentityAccess.IsUniqueViolation(exception))
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
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

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
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

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
}
