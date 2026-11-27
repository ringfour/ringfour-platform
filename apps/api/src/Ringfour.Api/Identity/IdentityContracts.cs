using Ringfour.Api.Persistence;

namespace Ringfour.Api.Identity;

public sealed record UserResponse(
    Guid Id,
    DateTimeOffset CreatedAt,
    string? Name,
    string? Email,
    string? ProfilePictureUrl,
    string? PhoneNumber)
{
    public static UserResponse FromEntity(User user) => new(
        user.Id, user.CreatedAt, user.Name, user.Email, user.ProfilePictureUrl, user.PhoneNumber);
}

public sealed record MembershipResponse(
    Guid OrganizationId,
    Guid UserId,
    OrgRole Role,
    DateTimeOffset CreatedAt,
    string? Name,
    string? Email,
    string? ProfilePictureUrl)
{
    public static MembershipResponse FromEntity(Membership membership) => new(
        membership.OrganizationId,
        membership.UserId,
        membership.Role,
        membership.CreatedAt,
        membership.User?.Name,
        membership.User?.Email,
        membership.User?.ProfilePictureUrl);
}

public sealed record MeResponse(UserResponse User, IReadOnlyList<MembershipResponse> Memberships);

public sealed record OrganizationResponse(Guid Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse FromEntity(Organization organization) =>
        new(organization.Id, organization.Name, organization.CreatedAt);
}

public sealed record OrganizationListItem(Guid Id, string Name, OrgRole Role, DateTimeOffset CreatedAt);

public sealed record OrganizationListResponse(
    IReadOnlyList<OrganizationListItem> Items,
    string? NextCursor);

public sealed record MemberListResponse(IReadOnlyList<MembershipResponse> Items, string? NextCursor);

public sealed record CreateOrganizationRequest(string? Name);

public sealed record UpdateUserProfileRequest(
    string? Name,
    string? ProfilePictureUrl,
    string? PhoneNumber);

public sealed record AddMemberRequest(Guid UserId, OrgRole Role);

public sealed record UpdateMemberRequest(OrgRole Role);

public sealed record CreateInvitationRequest(string? Email, OrgRole Role);

public sealed record CreateInvitationCodeRequest(OrgRole Role);

public sealed record InvitationTokenRequest(string? Token);

public sealed record OrganizationInvitationResponse(
    Guid Id,
    Guid OrganizationId,
    string? Email,
    OrgRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    Guid? AcceptedByUserId,
    DateTimeOffset? RevokedAt)
{
    public static OrganizationInvitationResponse FromEntity(OrganizationInvitation invitation) => new(
        invitation.Id,
        invitation.OrganizationId,
        invitation.Email,
        invitation.Role,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.AcceptedAt,
        invitation.AcceptedByUserId,
        invitation.RevokedAt);
}

public sealed record InvitationListResponse(IReadOnlyList<OrganizationInvitationResponse> Items);

public sealed record InvitationPreviewResponse(
    Guid OrganizationId,
    string OrganizationName,
    string? Email,
    OrgRole Role,
    DateTimeOffset ExpiresAt);
