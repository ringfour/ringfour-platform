using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace Ringfour.Api.Identity;

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").RequireAuthorization();

        group.MapGet("/me", GetMe);
        group.MapPatch("/me", UpdateMe);
        group.MapPost("/organizations", CreateOrganization);
        group.MapGet("/organizations", ListOrganizations);
        group.MapDelete("/organizations/{orgId:guid}", DeleteOrganization);
        group.MapGet("/organizations/{orgId:guid}/members", ListMembers);
        group.MapPost("/organizations/{orgId:guid}/members", AddMember);
        group.MapPatch("/organizations/{orgId:guid}/members/{userId:guid}", UpdateMemberRole);
        group.MapDelete("/organizations/{orgId:guid}/members/{userId:guid}", RemoveMember);
        group.MapDelete("/organizations/{orgId:guid}/members/me", LeaveOrganization);
        group.MapGet("/organizations/{orgId:guid}/invitations", ListInvitations);
        group.MapPost("/organizations/{orgId:guid}/invitations", CreateInvitation);
        group.MapPost("/organizations/{orgId:guid}/invitation-codes", CreateInvitationCode);
        group.MapPost("/organizations/{orgId:guid}/invitations/{invitationId:guid}/resend", ResendInvitation);
        group.MapDelete("/organizations/{orgId:guid}/invitations/{invitationId:guid}", RevokeInvitation);
        group.MapPost("/invitations/preview", PreviewInvitation);
        group.MapPost("/invitations/accept", AcceptInvitation);
    }

    private static async Task<MeResponse> GetMe(
        HttpContext httpContext,
        RingfourDbContext db,
        Auth0UserInfoClient userInfoClient,
        CancellationToken cancellationToken)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        var profile = await userInfoClient.GetProfileAsync(httpContext, cancellationToken);
        if (profile is not null)
        {
            user.Email = profile.EmailVerified ? profile.Email : null;
            await db.SaveChangesAsync(cancellationToken);
        }
        var memberships = await db.Memberships
            .Where(membership => membership.UserId == user.Id)
            .Include(membership => membership.User)
            .OrderBy(membership => membership.CreatedAt)
            .ThenBy(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken);
        return new MeResponse(
            UserResponse.FromEntity(user),
            memberships.Select(MembershipResponse.FromEntity).ToList());
    }

    private static async Task<UserResponse> UpdateMe(
        HttpContext httpContext,
        RingfourDbContext db,
        UpdateUserProfileRequest request,
        CancellationToken cancellationToken)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        user.Name = NormalizeOptional(request.Name, 120, "Name");
        user.ProfilePictureUrl = NormalizePictureUrl(request.ProfilePictureUrl);
        user.PhoneNumber = NormalizePhoneNumber(request.PhoneNumber);
        await db.SaveChangesAsync(cancellationToken);
        return UserResponse.FromEntity(user);
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
            .Include(membership => membership.User)
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

        var target = await db.Users.SingleOrDefaultAsync(
            user => user.Id == request.UserId, cancellationToken)
            ?? throw ApiException.NotFound("The target user does not exist.");

        var membership = new Membership
        {
            OrganizationId = orgId,
            UserId = request.UserId,
            Role = request.Role,
            CreatedAt = DateTimeOffset.UtcNow,
            User = target,
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

        await transaction.CommitAsync(cancellationToken);
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
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

        var target = await db.Memberships.Include(membership => membership.User).SingleOrDefaultAsync(
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

    private static async Task<IResult> DeleteOrganization(
        HttpContext httpContext, RingfourDbContext db,
        DeviceConnectionManager connections, Guid orgId, CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);
        // Serialize with App starts/installation changes before checking device work.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE organization_id = {orgId} ORDER BY id FOR UPDATE", cancellationToken);
        var robotIds = db.Robots.Where(r => r.OrganizationId == orgId).Select(r => r.Id);
        if (await db.AppInstallations.AnyAsync(i => i.OrganizationId == orgId && i.InstalledBinding != null, cancellationToken) ||
            await db.AppRuns.AnyAsync(r => r.OrganizationId == orgId && (r.State == "accepted" || r.State == "running"), cancellationToken) ||
            await db.AppOperations.AnyAsync(o => o.OrganizationId == orgId && (o.State == "queued" || o.State == "accepted" || o.State == "running"), cancellationToken) ||
            await db.RobotExecutionSlots.AnyAsync(slot => robotIds.Contains(slot.RobotId), cancellationToken))
            throw ApiException.Conflict("organization_has_apps", "Stop and uninstall all Apps and wait for pending operations before deleting this organization.");

        var now = DateTimeOffset.UtcNow;
        var organization = await db.Organizations.SingleAsync(o => o.Id == orgId, cancellationToken);
        organization.DeletedAt = now;
        await db.Memberships.Where(m => m.OrganizationId == orgId).ExecuteDeleteAsync(cancellationToken);
        await db.OrganizationInvitations.Where(i => i.OrganizationId == orgId && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(i => i.RevokedAt, now), cancellationToken);
        await db.RobotCredentials.Where(c => robotIds.Contains(c.RobotId) && c.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.RevokedAt, now), cancellationToken);
        await db.EnrollmentTokens.Where(t => robotIds.Contains(t.RobotId) && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.ExpiresAt, now), cancellationToken);
        await db.AppShares.Where(share => (share.PublisherOrganizationId == orgId || share.DestinationOrganizationId == orgId) && share.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(share => share.RevokedAt, now), cancellationToken);
        var ids = await robotIds.ToListAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var id in ids)
        {
            var session = connections.Get(id);
            if (session is null) continue;
            try { session.Socket.Abort(); }
            catch (ObjectDisposedException) { /* A concurrent disconnect already closed it. */ }
            connections.Unregister(id, session.SessionId);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> LeaveOrganization(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);

        // Resolve membership under the same lock as owner removals/demotions.
        // The route never accepts a different target user or requires Owner rights.
        var membership = await IdentityAccess.RequireMembership(
            db, orgId, caller.Id, OrgRole.Operator, cancellationToken);
        if (membership.Role == OrgRole.Owner &&
            await CountOwners(db, orgId, cancellationToken) <= 1)
        {
            throw ApiException.Conflict(
                "last_owner", "Make another member an owner before leaving this organization.");
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
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
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, orgId, cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);

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

    private static async Task<InvitationListResponse> ListInvitations(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);
        var invitations = await db.OrganizationInvitations
            .Where(invitation => invitation.OrganizationId == orgId)
            .OrderByDescending(invitation => invitation.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        return new InvitationListResponse(invitations.Select(OrganizationInvitationResponse.FromEntity).ToList());
    }

    private static async Task<IResult> CreateInvitationCode(
        HttpContext context, RingfourDbContext db, Guid orgId,
        CreateInvitationCodeRequest request, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrganization(db, orgId, ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Owner, ct);
        if (!Enum.IsDefined(request.Role)) throw ApiException.InvalidInput("Choose operator, developer or owner.");
        var code = CreateInviteToken();
        var now = DateTimeOffset.UtcNow;
        var invitation = new OrganizationInvitation { Id = Guid.NewGuid(), OrganizationId = orgId,
            Role = request.Role, InvitedByUserId = user.Id, TokenHash = HashToken(code), CreatedAt = now, ExpiresAt = now.AddDays(7) };
        db.OrganizationInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/v1/organizations/{orgId}/invitations/{invitation.Id}", new { invitation = OrganizationInvitationResponse.FromEntity(invitation), code });
    }

    private static async Task<IResult> CreateInvitation(
        HttpContext httpContext,
        RingfourDbContext db,
        InvitationEmailSender emailSender,
        Guid orgId,
        CreateInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);
        if (!Enum.IsDefined(request.Role))
        {
            throw ApiException.InvalidInput("Invitation role must be owner, developer or operator.");
        }
        var email = NormalizeEmail(request.Email);
        var organization = await db.Organizations.SingleOrDefaultAsync(
            candidate => candidate.Id == orgId, cancellationToken)
            ?? throw ApiException.NotFound("The organization does not exist.");

        var now = DateTimeOffset.UtcNow;
        var pending = await db.OrganizationInvitations.SingleOrDefaultAsync(invitation =>
            invitation.OrganizationId == orgId && invitation.NormalizedEmail == email.Normalized &&
            invitation.AcceptedAt == null && invitation.RevokedAt == null,
            cancellationToken);
        if (pending is not null)
        {
            throw ApiException.Conflict("invitation_pending", "An invitation already exists for this email. Resend it from the organization view.");
        }

        var token = CreateInviteToken();
        var invitation = new OrganizationInvitation
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Email = email.Address,
            NormalizedEmail = email.Normalized,
            Role = request.Role,
            InvitedByUserId = caller.Id,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
        };
        db.OrganizationInvitations.Add(invitation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IdentityAccess.IsUniqueViolation(exception))
        {
            throw ApiException.Conflict("invitation_pending", "An invitation already exists for this email. Resend it from the organization view.");
        }
        try
        {
            await emailSender.SendAsync(
                invitation.Email, organization.Name, invitation.Role, token,
                invitation.ExpiresAt, cancellationToken);
        }
        catch
        {
            db.OrganizationInvitations.Remove(invitation);
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }

        return Results.Created(
            $"/v1/organizations/{orgId}/invitations/{invitation.Id}",
            OrganizationInvitationResponse.FromEntity(invitation));
    }

    private static async Task<IResult> ResendInvitation(
        HttpContext httpContext,
        RingfourDbContext db,
        InvitationEmailSender emailSender,
        Guid orgId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockInvitation(db, invitationId, cancellationToken);
        var invitation = await db.OrganizationInvitations.Include(item => item.Organization)
            .SingleOrDefaultAsync(item => item.Id == invitationId && item.OrganizationId == orgId, cancellationToken)
            ?? throw ApiException.NotFound("The invitation does not exist in this organization.");
        if (invitation.AcceptedAt is not null || invitation.RevokedAt is not null)
        {
            throw ApiException.Conflict("invitation_closed", "Accepted or revoked invitations cannot be resent.");
        }

        var token = CreateInviteToken();
        if (invitation.Email is null) throw ApiException.InvalidInput("Create a new invitation code instead of resending email.");
        invitation.TokenHash = HashToken(token);
        invitation.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        await db.SaveChangesAsync(cancellationToken);
        await emailSender.SendAsync(
            invitation.Email, invitation.Organization.Name, invitation.Role, token,
            invitation.ExpiresAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(OrganizationInvitationResponse.FromEntity(invitation));
    }

    private static async Task<IResult> RevokeInvitation(
        HttpContext httpContext,
        RingfourDbContext db,
        Guid orgId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var caller = await IdentityAccess.ResolveOrCreateUser(
            db, IdentityAccess.RequireSubject(httpContext), cancellationToken);
        await IdentityAccess.RequireMembership(db, orgId, caller.Id, OrgRole.Owner, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockInvitation(db, invitationId, cancellationToken);
        var invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(
            item => item.Id == invitationId && item.OrganizationId == orgId, cancellationToken)
            ?? throw ApiException.NotFound("The invitation does not exist in this organization.");
        if (invitation.AcceptedAt is not null)
        {
            throw ApiException.Conflict("invitation_accepted", "An accepted invitation cannot be revoked.");
        }
        invitation.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PreviewInvitation(
        RingfourDbContext db,
        InvitationTokenRequest request,
        CancellationToken cancellationToken)
    {
        var token = RequireInvitationToken(request.Token);
        var invitation = await db.OrganizationInvitations.Include(item => item.Organization)
            .SingleOrDefaultAsync(item => item.TokenHash == HashToken(token) && item.Organization.DeletedAt == null, cancellationToken)
            ?? throw ApiException.NotFound("The invitation link is invalid or expired.");
        EnsureInvitationOpen(invitation);
        return Results.Ok(new InvitationPreviewResponse(
            invitation.OrganizationId, invitation.Organization.Name,
            invitation.Email, invitation.Role, invitation.ExpiresAt));
    }

    private static async Task<IResult> AcceptInvitation(
        HttpContext httpContext,
        RingfourDbContext db,
        Auth0UserInfoClient userInfoClient,
        InvitationTokenRequest request,
        CancellationToken cancellationToken)
    {
        var token = RequireInvitationToken(request.Token);
        var subject = IdentityAccess.RequireSubject(httpContext);
        var caller = await IdentityAccess.ResolveOrCreateUser(db, subject, cancellationToken);
        var tokenHash = HashToken(token);
        var invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(
            item => item.TokenHash == tokenHash, cancellationToken)
            ?? throw ApiException.NotFound("The invitation link is invalid or expired.");

        string? verifiedEmail = null;
        if (invitation.NormalizedEmail is not null)
        {
            var profile = await userInfoClient.GetVerifiedEmailAsync(httpContext, cancellationToken);
            if (profile.NormalizedAddress != invitation.NormalizedEmail)
                throw ApiException.Forbidden("Sign in with the verified email address that received this invitation.");
            verifiedEmail = profile.Address;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockOrganization(db, invitation.OrganizationId, cancellationToken);
        if (!await db.Organizations.AnyAsync(o => o.Id == invitation.OrganizationId && o.DeletedAt == null, cancellationToken))
            throw ApiException.NotFound("The invitation link is invalid or expired.");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM organization_invitations WHERE id = {invitation.Id} FOR UPDATE", cancellationToken);
        await db.Entry(invitation).ReloadAsync(cancellationToken);
        if (invitation.TokenHash != tokenHash)
        {
            throw ApiException.NotFound("The invitation link is invalid or expired.");
        }
        if (invitation.AcceptedAt is not null)
        {
            if (invitation.AcceptedByUserId == caller.Id)
            {
                var prior = await db.Memberships.SingleOrDefaultAsync(
                    item => item.OrganizationId == invitation.OrganizationId && item.UserId == caller.Id,
                    cancellationToken) ?? throw ApiException.NotFound("You have left this organization. Ask an owner for a new invitation.");
                return Results.Ok(MembershipResponse.FromEntity(prior));
            }
            throw ApiException.Conflict("invitation_accepted", "This invitation has already been accepted.");
        }
        EnsureInvitationOpen(invitation);
        var existingMembership = await db.Memberships.AnyAsync(
            item => item.OrganizationId == invitation.OrganizationId && item.UserId == caller.Id,
            cancellationToken);
        if (existingMembership)
        {
            throw ApiException.Conflict("already_a_member", "You are already a member of this organization.");
        }

        var membership = new Membership
        {
            OrganizationId = invitation.OrganizationId,
            UserId = caller.Id,
            Role = invitation.Role,
            CreatedAt = DateTimeOffset.UtcNow,
            User = caller,
        };
        if (verifiedEmail is not null) caller.Email = verifiedEmail;
        db.Memberships.Add(membership);
        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        invitation.AcceptedByUserId = caller.Id;
        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(membership).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(MembershipResponse.FromEntity(membership));
    }

    private static (string Address, string Normalized) NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320 ||
            !System.Net.Mail.MailAddress.TryCreate(value.Trim(), out var parsed) ||
            !string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.InvalidInput("Enter a valid email address.");
        }
        return (parsed.Address, parsed.Address.ToUpperInvariant());
    }

    private static string? NormalizeOptional(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength)
        {
            throw ApiException.InvalidInput($"{label} must not exceed {maxLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizePictureUrl(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 2048 ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw ApiException.InvalidInput("Profile picture must be an HTTPS URL without embedded credentials.");
        }
        return uri.AbsoluteUri;
    }

    private static string? NormalizePhoneNumber(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        var digitCount = normalized.Count(char.IsDigit);
        var validCharacters = normalized.All(character =>
            char.IsDigit(character) || character is '+' or ' ' or '(' or ')' or '-');
        if (normalized.Length > 32 || digitCount is < 7 or > 15 || !validCharacters ||
            normalized.Skip(1).Contains('+'))
        {
            throw ApiException.InvalidInput("Phone number must contain 7 to 15 digits and may use +, spaces, parentheses or hyphens.");
        }
        return normalized;
    }

    private static string CreateInviteToken() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string RequireInvitationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            throw ApiException.InvalidInput("A valid invitation token is required.");
        }
        return token;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task LockInvitation(
        RingfourDbContext db, Guid invitationId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM organization_invitations WHERE id = {invitationId} FOR UPDATE", cancellationToken);
    }

    private static void EnsureInvitationOpen(OrganizationInvitation invitation)
    {
        if (invitation.AcceptedAt is not null)
        {
            throw ApiException.Conflict("invitation_accepted", "This invitation has already been accepted.");
        }
        if (invitation.RevokedAt is not null)
        {
            throw ApiException.Conflict("invitation_revoked", "This invitation has been revoked.");
        }
        if (invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw ApiException.Conflict("invitation_expired", "This invitation has expired.");
        }
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
